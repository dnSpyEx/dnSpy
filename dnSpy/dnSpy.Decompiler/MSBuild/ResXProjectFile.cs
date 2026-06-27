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

using dnlib.DotNet;
using dnlib.DotNet.Resources;
using dnSpy.Decompiler.Properties;

namespace dnSpy.Decompiler.MSBuild {
	sealed class ResXProjectFile : ProjectFile {
		public override string Description => dnSpy_Decompiler_Resources.MSBuild_CreateResXFile;
		public override BuildAction BuildAction => BuildAction.EmbeddedResource;
		public override string Filename { get; }
		public string TypeFullName { get; }
		public bool IsSatelliteFile { get; set; }

		readonly ModuleDef module;
		readonly ResourceElementSet resourceElementSet;

		public ResXProjectFile(ModuleDef module, string filename, string typeFullName, ResourceElementSet resourceElementSet) {
			this.module = module;
			Filename = filename;
			TypeFullName = typeFullName;
			this.resourceElementSet = resourceElementSet;
		}

		public override void Create(DecompileContext ctx) {
			using (var writer = new ResXResourceFileWriter(Filename, module)) {
				foreach (var resourceElement in resourceElementSet.ResourceElements) {
					ctx.CancellationToken.ThrowIfCancellationRequested();
					writer.AddResourceData(resourceElement);
				}
			}
		}
	}
}
