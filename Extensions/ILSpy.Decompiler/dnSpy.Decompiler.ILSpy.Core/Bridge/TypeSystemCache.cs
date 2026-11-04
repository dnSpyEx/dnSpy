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
using System.Runtime.CompilerServices;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Caches the upstream type system of each bridged module. It's thread safe and expensive to create (it reads
	/// every referenced assembly). It's rebuilt when the module or one of the modules it references is invalidated,
	/// or when assemblies were added and one of its references couldn't be resolved.
	/// </summary>
	sealed class TypeSystemCache {
		readonly ModuleBridgeCache cache;
		readonly ConditionalWeakTable<BridgedModule, Entry> entries = new ConditionalWeakTable<BridgedModule, Entry>();

		sealed class Entry {
			public DecompilerTypeSystem? TypeSystem;
			public DnlibAssemblyResolver? Resolver;
			public TypeSystemOptions Options;
			public int AddedGeneration;
		}

		public TypeSystemCache(ModuleBridgeCache cache) => this.cache = cache ?? throw new ArgumentNullException(nameof(cache));

		/// <param name="bridged">Module</param>
		/// <param name="settings">Settings</param>
		/// <param name="useCache">false to create a type system that isn't cached and that doesn't cache the images
		/// of referenced modules (dnSpy resolves references to temporary modules while it doesn't load assemblies)</param>
		public DecompilerTypeSystem Get(BridgedModule bridged, DecompilerSettings settings, bool useCache = true) {
			if (!useCache)
				return new DecompilerTypeSystem(bridged.File, new DnlibAssemblyResolver(cache, bridged.Module, cacheImages: false), settings);

			int addedGeneration = cache.AddedGeneration;
			var options = DecompilerTypeSystem.GetOptions(settings);
			var entry = entries.GetValue(bridged, _ => new Entry());
			lock (entry) {
				if (entry.TypeSystem is null || entry.Resolver is null || entry.Options != options || !IsValid(entry, addedGeneration)) {
					var resolver = new DnlibAssemblyResolver(cache, bridged.Module);
					entry.TypeSystem = new DecompilerTypeSystem(bridged.File, resolver, settings);
					entry.Resolver = resolver;
					entry.Options = options;
					entry.AddedGeneration = addedGeneration;
				}
				return entry.TypeSystem;
			}
		}

		/// <summary>
		/// Creates a type system that isn't cached, for another image of a module (see
		/// <see cref="ModuleBridgeCache.AddImage"/>). The images of referenced modules are cached as usual.
		/// </summary>
		public DecompilerTypeSystem CreateForImage(BridgedModule image, DecompilerSettings settings) =>
			new DecompilerTypeSystem(image.File, new DnlibAssemblyResolver(cache, image.Module), settings);

		static bool IsValid(Entry entry, int addedGeneration) {
			var resolver = entry.Resolver!;
			if (resolver.UsesInvalidatedModule)
				return false;
			if (resolver.HadFailures && entry.AddedGeneration != addedGeneration)
				return false;
			return true;
		}
	}
}
