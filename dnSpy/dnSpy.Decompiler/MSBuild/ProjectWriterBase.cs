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
using System.Diagnostics;
using System.IO;
using System.Linq;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Utilities;

namespace dnSpy.Decompiler.MSBuild {
	abstract class ProjectWriterBase {
		protected readonly Project project;
		protected readonly ProjectVersion projectVersion;
		protected readonly IList<Project> allProjects;
		protected readonly IList<string> userGACPaths;

		protected ProjectWriterBase(Project project, ProjectVersion projectVersion, IList<Project> allProjects, IList<string> userGACPaths) {
			this.project = project;
			this.projectVersion = projectVersion;
			this.allProjects = allProjects;
			this.userGACPaths = userGACPaths;
		}

		public abstract void Write();

		protected static string GetRelativePath(string sourceDir, string destFile) {
			var s = FilenameUtils.GetRelativePath(sourceDir, destFile);
			if (Path.DirectorySeparatorChar != '\\')
				s = s.Replace(Path.DirectorySeparatorChar, '\\');
			if (Path.AltDirectorySeparatorChar != '\\')
				s = s.Replace(Path.AltDirectorySeparatorChar, '\\');
			return s;
		}

		protected string GetRelativePath(string filename) => GetRelativePath(project.Directory, filename);

		protected static string ToString(BuildAction buildAction) => buildAction switch {
			BuildAction.None => "None",
			BuildAction.Compile => "Compile",
			BuildAction.EmbeddedResource => "EmbeddedResource",
			BuildAction.ApplicationDefinition => "ApplicationDefinition",
			BuildAction.Page => "Page",
			BuildAction.Resource => "Resource",
			BuildAction.SplashScreen => "SplashScreen",
			_ => throw new InvalidOperationException()
		};

		protected string GetPlatformString() {
			var machine = project.Module.Machine;
			if (machine.IsI386()) {
				int c = (project.Module.Is32BitRequired ? 2 : 0) + (project.Module.Is32BitPreferred ? 1 : 0);
				switch (c) {
				case 0: // no special meaning, MachineType and ILONLY flag determine image requirements
					if (!project.Module.IsILOnly)
						return "x86";
					return "AnyCPU";
				case 1: // illegal, reserved for future use
					break;
				case 2: // image is x86-specific
					return "x86";
				case 3: // image is project.Platform neutral and prefers to be loaded 32-bit when possible
					return "AnyCPU";
				}
				return "AnyCPU";
			}
			if (machine.IsAMD64())
				return "x64";
			if (machine == Machine.IA64)
				return "Itanium";
			if (machine.IsARMNT())
				return "ARM";
			if (machine.IsARM64())
				return "ARM64";
			Debug.Fail("Unknown machine");
			return machine.ToString();
		}

		protected string GetOutputType() {
			if (project.Module.IsWinMD)
				return "WinMDObj";
			switch (project.Module.Kind) {
			case ModuleKind.Console:	return "Exe";
			case ModuleKind.Windows:	return "WinExe";
			case ModuleKind.Dll:		return "Library";
			case ModuleKind.NetModule:	return "Module";

			default:
				Debug.Fail("Unknown module kind: " + project.Module.Kind);
				return "Library";
			}
		}

		/// <summary>
		/// Writes the properties the decompiler's code needs (<see cref="IProjectPropertiesProvider"/>), e.g. LangVersion
		/// </summary>
		protected void WriteDecompilerProperties(System.Xml.XmlWriter writer) {
			if (project.Options.Decompiler is not IProjectPropertiesProvider provider)
				return;
			foreach (var kv in provider.GetProjectProperties(project.Module))
				writer.WriteElementString(kv.Key, kv.Value);
		}

		protected string GetRootNamespace() => string.IsNullOrEmpty(project.DefaultNamespace) ? GetAssemblyName() : project.DefaultNamespace;

		protected string GetAssemblyName() => project.AssemblyName;

		protected string GetFileAlignment() {
			if (project.Module is ModuleDefMD mod)
				return mod.Metadata.PEImage.ImageNTHeaders.OptionalHeader.FileAlignment.ToString();
			return "512";
		}

		protected string? GetHintPath(AssemblyDef? asm) {
			if (asm is null)
				return null;
			if (IsGacPath(asm.ManifestModule.Location))
				return null;
			if (ExistsInProject(asm.ManifestModule.Location))
				return null;

			return GetRelativePath(asm.ManifestModule.Location);
		}

		bool IsGacPath(string file) => GacInfo.IsGacPath(file) || IsUserGacPath(file);

		bool IsUserGacPath(string file) {
			file = file.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
			foreach (var dir in userGACPaths) {
				if (file.StartsWith(dir + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		protected bool ExistsInProject(string filename) => FindOtherProject(filename) is not null;

		protected bool AssemblyExistsInProject(string asmSimpleName) =>
			allProjects.Any(a => StringComparer.OrdinalIgnoreCase.Equals(a.AssemblyName, asmSimpleName));

		protected Project? FindOtherProject(string filename) =>
			allProjects.FirstOrDefault(f => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(f.Module.Location), Path.GetFullPath(filename)));
	}
}
