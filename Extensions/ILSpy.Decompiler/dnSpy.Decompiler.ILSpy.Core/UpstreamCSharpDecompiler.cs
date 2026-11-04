/*
    Copyright (C) 2026 dnSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using dnSpy.Decompiler.ILSpy.Core.Output;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core {
	sealed class UpstreamDecompilationResult {
		public string Text { get; }
		public IReadOnlyList<OutputReference> References { get; }
		public IReadOnlyList<MethodDebugInfo> MethodDebugInfos { get; }
		public BridgedModule Module { get; }

		/// <summary>
		/// The upstream decompiler's own sequence points (for diagnostics)
		/// </summary>
		public IReadOnlyDictionary<ICSharpCode.Decompiler.IL.ILFunction, List<ICSharpCode.Decompiler.DebugInfo.SequencePoint>>? RawSequencePoints { get; set; }

		/// <summary>
		/// Upstream sequence points that were dropped or fixed because they were invalid
		/// </summary>
		public IReadOnlyList<string> DebugInfoRepairs { get; set; } = Array.Empty<string>();

		public UpstreamDecompilationResult(string text, IReadOnlyList<OutputReference> references, IReadOnlyList<MethodDebugInfo> methodDebugInfos, BridgedModule module) {
			Text = text;
			References = references;
			MethodDebugInfos = methodDebugInfos;
			Module = module;
		}
	}

	/// <summary>
	/// Decompiles dnlib types/members to C# with the unmodified upstream ILSpy engine
	/// </summary>
	sealed class UpstreamCSharpDecompiler {
		readonly ModuleBridgeCache cache;
		readonly DecompilerSettings settings;
		readonly TypeSystemCache typeSystems;

		public ModuleBridgeCache Cache => cache;

		public UpstreamCSharpDecompiler(ModuleBridgeCache cache, DecompilerSettings settings) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.settings = settings ?? throw new ArgumentNullException(nameof(settings));
			typeSystems = new TypeSystemCache(cache);
		}

		public UpstreamDecompilationResult DecompileType(TypeDef type, CancellationToken cancellationToken = default) {
			if (type is null)
				throw new ArgumentNullException(nameof(type));
			return Decompile(type.Module, type, (decompiler, handle) => decompiler.DecompileTypes(new[] { (TypeDefinitionHandle)handle }), cancellationToken);
		}

		public UpstreamDecompilationResult DecompileMember(IMemberDef member, CancellationToken cancellationToken = default) {
			if (member is null)
				throw new ArgumentNullException(nameof(member));
			return Decompile(member.Module, member, (decompiler, handle) => decompiler.Decompile(handle), cancellationToken);
		}

		UpstreamDecompilationResult Decompile(ModuleDef module, IMDTokenProvider definition, Func<CSharpDecompiler, EntityHandle, SyntaxTree> decompile, CancellationToken cancellationToken) {
			var bridged = cache.Get(module);
			int token = bridged.Tokens.GetToken(definition);
			if (token == 0)
				throw new ArgumentException($"{definition} isn't part of the bridged image of module '{module.Name}'");

			var typeSystem = typeSystems.Get(bridged, settings);
			var decompiler = new CSharpDecompiler(typeSystem, settings) {
				CancellationToken = cancellationToken,
			};
			var syntaxTree = decompile(decompiler, MetadataTokens.EntityHandle(token));

			var output = new TrackingTextOutput();
			syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
			var tokenWriter = TokenWriter.InsertRequiredSpaces(TokenWriter.WrapInWriterThatSetsLocationsInAST(new LocatableTextTokenWriter(output, settings)));
			syntaxTree.AcceptVisitor(new DnSpyOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));

			var sequencePoints = decompiler.CreateSequencePoints(syntaxTree);
			var repairs = new List<string>();
			var variables = new SourceVariableTable(cache, new TypeSigConverter(cache, module));
			var debugInfos = DebugInfoFactory.Create(syntaxTree, sequencePoints, output, cache, variables, decompilerSettingsVersion: 0, repairs);
			return new UpstreamDecompilationResult(output.ToString(), output.References, debugInfos, bridged) {
				RawSequencePoints = sequencePoints,
				DebugInfoRepairs = repairs,
			};
		}
	}
}
