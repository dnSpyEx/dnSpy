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
using dnlib.DotNet;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Caches the bridged image of each module and maps upstream entities back to dnlib definitions.
	/// Thread safe.
	/// </summary>
	sealed class ModuleBridgeCache {
		readonly object lockObj = new object();
		readonly Dictionary<ModuleDef, BridgedModule> byModule = new Dictionary<ModuleDef, BridgedModule>();
		readonly Dictionary<ModuleDef, ModuleBridgeException> failures = new Dictionary<ModuleDef, ModuleBridgeException>();
		// Every image handed out, cached or not, so entities of a decompilation that's still using an image that
		// was invalidated (or never cached) can still be mapped back to dnlib
		readonly ConditionalWeakTable<MetadataFile, BridgedModule> allFiles = new ConditionalWeakTable<MetadataFile, BridgedModule>();
		readonly Func<ModuleDef, bool> isModified;
		int generation;
		int addedGeneration;

		public ModuleBridgeOptions Options { get; }

		/// <summary>
		/// Incremented whenever a module's image is invalidated
		/// </summary>
		public int Generation {
			get {
				lock (lockObj)
					return generation;
			}
		}

		/// <summary>
		/// Incremented when assemblies were added: references that couldn't be resolved may now resolve
		/// </summary>
		public int AddedGeneration {
			get {
				lock (lockObj)
					return addedGeneration;
			}
		}

		/// <param name="options">Options</param>
		/// <param name="isModified">Returns true if a module has been edited in memory at any time since it was
		/// loaded, even if it has been saved since (dnSpy doesn't reload saved modules)</param>
		public ModuleBridgeCache(ModuleBridgeOptions options, Func<ModuleDef, bool>? isModified = null) {
			Options = options ?? throw new ArgumentNullException(nameof(options));
			this.isModified = isModified ?? (_ => false);
		}

		/// <summary>
		/// Gets the bridged image of a module
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="cacheResult">false to not cache a new image (eg. a temporary module that dnSpy doesn't show)</param>
		public BridgedModule Get(ModuleDef module, bool cacheResult = true) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			int startGeneration;
			lock (lockObj) {
				if (byModule.TryGetValue(module, out var bridged))
					return bridged;
				if (failures.TryGetValue(module, out var failure))
					throw failure;
				startGeneration = generation;
			}
			// Created outside the lock: it can take a while and it may need to bridge other modules
			BridgedModule created;
			try {
				created = ModuleBridge.Create(module, isModified(module), Options);
			}
			catch (ModuleBridgeException ex) {
				lock (lockObj) {
					if (generation == startGeneration && cacheResult)
						failures[module] = ex;
				}
				throw;
			}
			lock (lockObj) {
				if (byModule.TryGetValue(module, out var bridged)) {
					created.Dispose();
					return bridged;
				}
				allFiles.Add(created.File, created);
				// Invalidated while we were creating it: the result may be stale, don't cache it
				if (generation != startGeneration || !cacheResult)
					created.IsInvalidated = generation != startGeneration;
				else
					byModule.Add(module, created);
				return created;
			}
		}

		/// <summary>
		/// Forgets the bridged image of a module, call it when the module has been edited or reloaded
		/// </summary>
		public void Invalidate(ModuleDef module) {
			lock (lockObj) {
				generation++;
				failures.Remove(module);
				if (byModule.TryGetValue(module, out var bridged)) {
					byModule.Remove(module);
					bridged.IsInvalidated = true;
				}
			}
		}

		/// <summary>
		/// Call it when assemblies were added (references that couldn't be resolved may now resolve)
		/// </summary>
		public void OnAssembliesAdded() {
			lock (lockObj)
				addedGeneration++;
		}

		/// <summary>
		/// Forgets everything, e.g. when assemblies were removed or reloaded
		/// </summary>
		public void InvalidateAll() {
			lock (lockObj) {
				generation++;
				addedGeneration++;
				failures.Clear();
				foreach (var bridged in byModule.Values)
					bridged.IsInvalidated = true;
				byModule.Clear();
			}
		}

		/// <summary>
		/// Registers another image of a bridged module with the same tokens (e.g. one with some method bodies
		/// replaced) so its entities map back to dnlib. It isn't cached.
		/// </summary>
		public BridgedModule AddImage(BridgedModule bridged, MetadataFile file) {
			var image = new BridgedModule(bridged.Module, file, bridged.Tokens, bridged.ImageKind, bridged.NormalizeReasons, bridged.WriterErrors, bridged.CreationTime) {
				IsInvalidated = bridged.IsInvalidated,
			};
			lock (lockObj)
				allFiles.Add(file, image);
			return image;
		}

		/// <summary>
		/// Gets the bridged module of an image (also images that were invalidated since)
		/// </summary>
		public BridgedModule? Find(MetadataFile file) {
			lock (lockObj)
				return allFiles.TryGetValue(file, out var bridged) ? bridged : null;
		}

		/// <summary>
		/// Gets the dnlib definition of an upstream entity, or null if it's not defined in a bridged module
		/// </summary>
		public IMDTokenProvider? ToDnlib(IEntity entity) {
			if (entity is null)
				throw new ArgumentNullException(nameof(entity));
			if (entity is IMember member && member.MemberDefinition is IEntity memberDef && !ReferenceEquals(memberDef, entity))
				entity = memberDef;
			return ToDnlib(entity.ParentModule?.MetadataFile, entity.MetadataToken);
		}

		public IMDTokenProvider? ToDnlib(MetadataFile? file, EntityHandle handle) {
			if (file is null || handle.IsNil)
				return null;
			return Find(file)?.Tokens.Resolve(MetadataTokens.GetToken(handle));
		}
	}
}
