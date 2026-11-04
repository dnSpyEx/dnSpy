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
using System.Threading;
using dnlib.DotNet;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugSteps;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.IL.Transforms;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core {
	/// <summary>
	/// The upstream decompiler's ILAst of a method, for code outside the C# decompiler that analyzes method bodies
	/// (e.g. the BAML decompiler reads IComponentConnector.Connect). Its entities map back to dnlib.
	/// </summary>
	public sealed class UpstreamILAst {
		// The images and type systems are shared by all users and kept until the module is edited or removed (the C#
		// language forwards those events)
		static readonly ModuleBridgeCache cache = new ModuleBridgeCache(new ModuleBridgeOptions(), ModuleEditTracker.Shared.IsModified);
		static readonly TypeSystemCache typeSystems = new TypeSystemCache(cache);
		static readonly DecompilerSettings settings = new DecompilerSettings(ICSharpCode.Decompiler.CSharp.LanguageVersion.Latest) {
			ThrowOnAssemblyResolveErrors = false,
		};

		internal static void OnModulesModified(IEnumerable<ModuleDef> modules) {
			foreach (var module in modules)
				cache.Invalidate(module);
		}

		internal static void OnModulesAdded() => cache.OnAssembliesAdded();

		internal static void OnModulesRemoved(IEnumerable<ModuleDef> modules) {
			foreach (var module in modules)
				cache.Invalidate(module);
		}

		internal static void OnAllModulesRemoved() => cache.InvalidateAll();

		/// <summary>
		/// The method's ILAst
		/// </summary>
		public ILFunction Function { get; }

		/// <summary>
		/// The type system of the method's module
		/// </summary>
		public IDecompilerTypeSystem TypeSystem { get; }

		UpstreamILAst(ILFunction function, IDecompilerTypeSystem typeSystem) {
			Function = function;
			TypeSystem = typeSystem;
		}

		/// <summary>
		/// Reads a method's IL into the upstream ILAst
		/// </summary>
		/// <param name="method">Method with a body</param>
		/// <param name="runTransforms">Run the IL transforms the C# decompiler runs (structured control flow, inlining,
		/// delegate construction...), else the ILAst is the IL as read</param>
		/// <param name="cancellationToken">Cancellation token</param>
		/// <returns>The ILAst, or null if the method has no body or its module can't be read</returns>
		public static UpstreamILAst? TryCreate(MethodDef method, bool runTransforms, CancellationToken cancellationToken) {
			if (method is null)
				throw new ArgumentNullException(nameof(method));
			var module = method.Module;
			if (module is null || !method.HasBody)
				return null;
			try {
				var bridged = cache.Get(module);
				if (GetBody(bridged, method) is not var (handle, body))
					return null;
				var typeSystem = typeSystems.Get(bridged, settings);
				var mainModule = typeSystem.MainModule;
				var genericContext = GetGenericContext(mainModule.GetDefinition(handle));
				var function = new ILReader(mainModule).ReadIL(handle, body, genericContext, ILFunctionKind.TopLevelFunction, cancellationToken);
				if (runTransforms) {
					var context = new ILTransformContext(function, typeSystem, null, settings) {
						CancellationToken = cancellationToken,
					};
					function.RunTransforms(CSharpDecompiler.GetILTransforms(), context);
				}
				return new UpstreamILAst(function, typeSystem);
			}
			catch (Exception ex) when (ex is ModuleBridgeException || ex is BadImageFormatException || ex is InvalidOperationException || ex is MetadataFileNotSupportedException) {
				return null;
			}
		}

		/// <summary>
		/// The handle and body of a method in an image, or null if it has no body there
		/// </summary>
		internal static (MethodDefinitionHandle handle, MethodBodyBlock body)? GetBody(BridgedModule bridged, MethodDef method) {
			int token = bridged.Tokens.GetToken(method);
			if ((token >> 24) != 0x06)
				return null;
			var handle = MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF);
			var metadataMethod = bridged.File.Metadata.GetMethodDefinition(handle);
			if (metadataMethod.RelativeVirtualAddress == 0)
				return null;
			return (handle, bridged.File.GetMethodBody(metadataMethod.RelativeVirtualAddress));
		}

		internal static GenericContext GetGenericContext(ICSharpCode.Decompiler.TypeSystem.IMethod method) =>
			new GenericContext(method.DeclaringTypeDefinition?.TypeParameters, method.TypeParameters);

		/// <summary>
		/// Runs the IL transforms the C# decompiler runs and records them in <paramref name="stepper"/>, which throws
		/// <see cref="StepLimitReachedException"/> when its step limit is reached. Release builds of the upstream
		/// decompiler don't record the steps of its transforms (they're compiled out), so each transform is one step;
		/// a build that records them (<see cref="Stepper.SteppingAvailable"/>) records its own.
		/// </summary>
		internal static void RunTransforms(ILFunction function, ILTransformContext context, Stepper stepper) {
			context.Stepper = stepper;
			var transforms = CSharpDecompiler.GetILTransforms();
			if (Stepper.SteppingAvailable) {
				function.RunTransforms(transforms, context);
				return;
			}
			foreach (var transform in transforms) {
				context.CancellationToken.ThrowIfCancellationRequested();
				stepper.StartGroup(transform is BlockILTransform ? transform.ToString()! : transform.GetType().Name);
				transform.Run(function, context);
				stepper.EndGroup(keepIfEmpty: true);
			}
		}

		/// <summary>
		/// Gets the dnlib definition of an entity (in the method's module or a module it references), or null if it
		/// isn't defined in a module dnSpy has loaded
		/// </summary>
		public IMDTokenProvider? ToDnlib(IEntity entity) => cache.ToDnlib(entity);
	}
}
