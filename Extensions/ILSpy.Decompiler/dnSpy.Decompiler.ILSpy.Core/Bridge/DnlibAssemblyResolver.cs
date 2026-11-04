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
using System.Threading.Tasks;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Resolves assemblies for the upstream decompiler with dnlib's resolver (in dnSpy: the documents in the
	/// assembly explorer), so references resolve to exactly the modules the user sees.
	/// </summary>
	sealed class DnlibAssemblyResolver : ICSharpCode.Decompiler.Metadata.IAssemblyResolver {
		readonly ModuleBridgeCache cache;
		readonly ModuleDef sourceModule;
		readonly bool cacheImages;
		readonly List<BridgedModule> resolvedModules = new List<BridgedModule>();
		bool hadFailures;

		/// <param name="cache">Cache</param>
		/// <param name="sourceModule">Module whose references are resolved</param>
		/// <param name="cacheImages">false to not cache the images of modules that aren't cached yet (eg. dnSpy
		/// resolves references to temporary modules while it doesn't load assemblies)</param>
		public DnlibAssemblyResolver(ModuleBridgeCache cache, ModuleDef sourceModule, bool cacheImages = true) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.sourceModule = sourceModule ?? throw new ArgumentNullException(nameof(sourceModule));
			this.cacheImages = cacheImages;
		}

		/// <summary>
		/// True if a reference couldn't be resolved (adding assemblies can change the result)
		/// </summary>
		public bool HadFailures {
			get {
				lock (resolvedModules)
					return hadFailures;
			}
		}

		/// <summary>
		/// True if one of the resolved modules' images has been invalidated
		/// </summary>
		public bool UsesInvalidatedModule {
			get {
				lock (resolvedModules) {
					foreach (var bridged in resolvedModules) {
						if (bridged.IsInvalidated)
							return true;
					}
					return false;
				}
			}
		}

		public MetadataFile? Resolve(IAssemblyReference reference) {
			var asmRef = new AssemblyRefUser(reference.Name, reference.Version ?? new Version(0, 0, 0, 0),
				reference.PublicKeyToken is byte[] pkt && pkt.Length != 0 ? new PublicKeyToken(pkt) : null,
				reference.Culture ?? string.Empty);
			AssemblyDef? asm;
			try {
				asm = sourceModule.Context.AssemblyResolver.Resolve(asmRef, sourceModule);
			}
			catch (Exception ex) when (ex is not OutOfMemoryException) {
				return null;
			}
			return TryBridge(asm?.ManifestModule);
		}

		public MetadataFile? ResolveModule(MetadataFile mainModule, string moduleName) {
			var asm = cache.Find(mainModule)?.Module.Assembly;
			if (asm is null)
				return null;
			foreach (var module in asm.Modules) {
				if (StringComparer.OrdinalIgnoreCase.Equals(UTF8String.ToSystemStringOrEmpty(module.Name), moduleName))
					return TryBridge(module);
			}
			return null;
		}

		public Task<MetadataFile?> ResolveAsync(IAssemblyReference reference) => Task.FromResult(Resolve(reference));

		public Task<MetadataFile?> ResolveModuleAsync(MetadataFile mainModule, string moduleName) => Task.FromResult(ResolveModule(mainModule, moduleName));

		public IDisposable? BeginSnapshot() => null;

		MetadataFile? TryBridge(ModuleDef? module) {
			BridgedModule? bridged = null;
			if (module is not null) {
				try {
					bridged = cache.Get(module, cacheImages);
				}
				catch (ModuleBridgeException) {
					// Decompile anyway, the types in this module will be unresolved
				}
			}
			lock (resolvedModules) {
				if (bridged is null)
					hadFailures = true;
				else
					resolvedModules.Add(bridged);
			}
			return bridged?.File;
		}
	}
}
