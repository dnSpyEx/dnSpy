/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using dnlib.DotNet;
using dnSpy.AsmEditor.Compiler.MDEditor;
using dnSpy.Contracts.ETW;

namespace dnSpy.AsmEditor.Compiler {
	readonly struct ModulePatcher {
		readonly RawModuleBytes moduleData;
		readonly bool isFileLayout;
		readonly IAssembly tempAssembly;
		readonly ModuleDef editedModule;
		readonly TypeDef? nonNestedEditedType;
		readonly Func<AssemblyRef, Version?>? getResolvedVersion;
		readonly Func<ModuleDef, List<TypeDef>>? getClashingTypes;

		/// <param name="getResolvedVersion">Version of the assembly that the compiler gets for a reference, or null</param>
		/// <param name="getClashingTypes">Public types of a module that clash with the edited module's own or friend types
		/// (see <see cref="CompilerVisibleTypes.GetClashingTypes"/>), or null</param>
		public ModulePatcher(RawModuleBytes moduleData, bool isFileLayout, IAssembly tempAssembly, ModuleDef editedModule, TypeDef? nonNestedEditedType,
			Func<AssemblyRef, Version?>? getResolvedVersion = null, Func<ModuleDef, List<TypeDef>>? getClashingTypes = null) {
			this.moduleData = moduleData;
			this.isFileLayout = isFileLayout;
			this.tempAssembly = tempAssembly;
			this.editedModule = editedModule;
			this.nonNestedEditedType = nonNestedEditedType;
			this.getResolvedVersion = getResolvedVersion;
			this.getClashingTypes = getClashingTypes;
		}

		public unsafe bool Patch(ModuleDef module, out RawModuleBytes newModuleData) {
			var moduleData = this.moduleData;

			// NOTE: We can't remove the type from the corlib (eg. mscorlib) because the compiler
			// (Roslyn) won't recognize it as the corlib if it has any AssemblyRefs.
			// A possible fix is to add a new netmodule to the corlib assembly.
			bool fixTypeDefRefs = nonNestedEditedType is not null &&
				MDPatcherUtils.ExistsInMetadata(nonNestedEditedType) &&
				MDPatcherUtils.ReferencesModule(module, nonNestedEditedType?.Module) &&
				!module.Assembly.IsCorLib();
			bool addIVT = module == editedModule || MDPatcherUtils.HasModuleInternalAccess(module, editedModule);
			// (The edited code sees all internal types of the modules it has internal access to)
			var clashingTypes = addIVT ? null : getClashingTypes?.Invoke(module);
			bool hideTypes = clashingTypes is not null && clashingTypes.Count != 0;
			if (fixTypeDefRefs || addIVT || hideTypes) {
				DnSpyEventSource.Log.EditCodePatchModuleStart(module.Location);
				using (var md = MDPatcherUtils.TryCreateMetadata(moduleData, isFileLayout)) {
					if (md is null)
						throw new InvalidOperationException("Couldn't create metadata");
					var mdEditor = new MetadataEditor(moduleData, md);
					var options = MDEditorPatcherOptions.None;
					if (fixTypeDefRefs)
						options |= MDEditorPatcherOptions.UpdateTypeReferences;
					if (addIVT)
						options |= MDEditorPatcherOptions.AllowInternalAccess;
					if (module == editedModule)
						options |= MDEditorPatcherOptions.MakeCompilerEmbeddedTypesVisible;
					if (hideTypes)
						options |= MDEditorPatcherOptions.HideTypes;
					// (Only modules that are rewritten anyway: the edited module and the ones that reference its type)
					if (getResolvedVersion is not null)
						options |= MDEditorPatcherOptions.UpdateAssemblyRefVersions;
					var patcher = new MDEditorPatcher(moduleData, mdEditor, tempAssembly, nonNestedEditedType, options, getResolvedVersion, clashingTypes);
					patcher.Patch(module);
					if (mdEditor.MustRewriteMetadata()) {
						var stream = new MDWriterMemoryStream();
						new MDWriter(moduleData, mdEditor, stream).Write();
						NativeMemoryRawModuleBytes? newRawData = null;
						try {
							newRawData = new NativeMemoryRawModuleBytes((int)stream.Length, isFileLayout: true);
							stream.CopyTo((IntPtr)newRawData.Pointer, newRawData.Size);
							moduleData = newRawData;
						}
						catch {
							newRawData?.Dispose();
							throw;
						}
					}
				}
				DnSpyEventSource.Log.EditCodePatchModuleStop(module.Location);
			}

			newModuleData = moduleData;
			return true;
		}
	}
}
