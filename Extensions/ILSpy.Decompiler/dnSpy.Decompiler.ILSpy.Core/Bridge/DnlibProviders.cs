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
using System.Text;
using System.Xml;
using dnlib.DotNet;
using dnlib.DotNet.Pdb;
using dnSpy.Contracts.Decompiler.XmlDoc;
using ICSharpCode.Decompiler.DebugInfo;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// XML documentation from the files dnSpy finds (it also looks in the reference assembly directories)
	/// </summary>
	sealed class DnlibDocumentationProvider : IDocumentationProvider {
		readonly ModuleBridgeCache cache;
		readonly StringBuilder sb = new StringBuilder();

		public DnlibDocumentationProvider(ModuleBridgeCache cache) => this.cache = cache ?? throw new ArgumentNullException(nameof(cache));

		public string? GetDocumentation(IEntity entity) {
			if (!(cache.ToDnlib(entity) is IMemberRef member) || member.Module is not ModuleDef module)
				return null;
			try {
				var provider = dnSpy.Contracts.Decompiler.XmlDoc.XmlDocLoader.LoadDocumentation(module);
				if (provider is null)
					return null;
				lock (sb) {
					return provider.GetDocumentation(XmlDocKeyProvider.GetKey(member, sb)) ??
						provider.GetDocumentation(XmlDocKeyProvider.GetKey(member, sb, XmlDocCompiler.MSVC));
				}
			}
			catch (XmlException) {
				return null;
			}
		}
	}

	/// <summary>
	/// Local variable names from the PDB file dnlib loaded (dnSpy's "load PDB files" option)
	/// </summary>
	sealed class DnlibDebugInfoProvider : IDebugInfoProvider {
		readonly BridgedModule bridged;

		DnlibDebugInfoProvider(BridgedModule bridged) => this.bridged = bridged;

		public static IDebugInfoProvider? TryCreate(BridgedModule bridged) =>
			bridged.Module.PdbState is null ? null : new DnlibDebugInfoProvider(bridged);

		public string Description => "dnlib: " + bridged.Module.Name;
		public string SourceFileName => string.Empty;

		MethodDef? GetMethod(MethodDefinitionHandle handle) =>
			bridged.Tokens.Resolve(MetadataTokens.GetToken(handle)) as MethodDef;

		public IList<ICSharpCode.Decompiler.DebugInfo.SequencePoint> GetSequencePoints(MethodDefinitionHandle method) =>
			Array.Empty<ICSharpCode.Decompiler.DebugInfo.SequencePoint>();

		public IList<Variable> GetVariables(MethodDefinitionHandle method) {
			var list = new List<Variable>();
			foreach (var kv in GetNames(method))
				list.Add(new Variable(kv.Key, kv.Value));
			return list;
		}

		public bool TryGetName(MethodDefinitionHandle method, int index, out string name) {
			if (GetNames(method).TryGetValue(index, out var n)) {
				name = n;
				return true;
			}
			name = null!;
			return false;
		}

		Dictionary<int, string> GetNames(MethodDefinitionHandle method) {
			var names = new Dictionary<int, string>();
			var scope = GetMethod(method)?.Body?.PdbMethod?.Scope;
			if (scope is not null)
				AddNames(scope, names, 0);
			return names;
		}

		static void AddNames(PdbScope scope, Dictionary<int, string> names, int depth) {
			if (depth > 100)
				return;
			foreach (var local in scope.Variables) {
				if (!string.IsNullOrEmpty(local.Name) && !names.ContainsKey(local.Index))
					names.Add(local.Index, local.Name);
			}
			foreach (var nested in scope.Scopes)
				AddNames(nested, names, depth + 1);
		}

		public bool TryGetExtraTypeInfo(MethodDefinitionHandle method, int index, out PdbExtraTypeInfo extraTypeInfo) {
			extraTypeInfo = default;
			return false;
		}
	}
}
