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
using System.IO;
using System.Threading;
using dnlib.DotNet;
using dnlib.PE;
using dnSpy.Contracts.Utilities;
using dnSpy.Decompiler;

namespace dnSpy.AsmEditor.Compiler {
	readonly struct MetadataReferenceFinder {
		readonly ModuleDef module;
		readonly CancellationToken cancellationToken;
		readonly Dictionary<IAssembly, AssemblyDef> assemblies;
		readonly HashSet<IAssembly> checkedContractsAssemblies;
		readonly DotNetPathProvider dotNetPathProvider;
		readonly TargetFrameworkInfo targetFrameworkInfo;
		// The target is a reference's newer .NET (GetCompileTarget)
		readonly bool targetFromReference;
		readonly Dictionary<IAssembly, AssemblyDef> referenceAssemblies;

		public MetadataReferenceFinder(ModuleDef module, CancellationToken cancellationToken) {
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			this.cancellationToken = cancellationToken;
			assemblies = new Dictionary<IAssembly, AssemblyDef>(new AssemblyNameComparer(AssemblyNameComparerFlags.All & ~AssemblyNameComparerFlags.Version));
			checkedContractsAssemblies = new HashSet<IAssembly>(AssemblyNameComparer.CompareAll);
			dotNetPathProvider = new DotNetPathProvider();
			(targetFrameworkInfo, targetFromReference) = GetCompileTarget(module, dotNetPathProvider);
			referenceAssemblies = new Dictionary<IAssembly, AssemblyDef>(AssemblyNameComparer.CompareAll);
		}

		/// <summary>
		/// The framework whose reference assemblies the code compiles against: the module's, or the newest .NET of its
		/// references. A .NET 9 (or .NET Standard) library of a .NET 10 app has .NET 10 references (e.g.
		/// Microsoft.Extensions 10 instead of the 9 it was built with) that use System.Runtime 10, which don't compile
		/// against .NET 9's (CS1705). The app runs it on .NET 10, so that's what it can be compiled against.
		/// A .NET Standard library can also use packages that aren't part of .NET Standard (e.g.
		/// System.Collections.Immutable, System.Reflection.Metadata) that are part of .NET. If the package's .NET
		/// Standard build isn't next to it, the code is compiled against .NET: the one they're resolved from, else the
		/// newest one. (With .NET Standard's reference assemblies they were missing (CS0234) or .NET's runtime assemblies
		/// (two core libraries, netstandard and System.Private.CoreLib: CS0518, CS0433).)
		/// </summary>
		static (TargetFrameworkInfo info, bool fromReference) GetCompileTarget(ModuleDef module, DotNetPathProvider dotNetPathProvider) {
			var info = TargetFrameworkInfo.Create(module);
			if (info.Framework != ".NETCoreApp" && info.Framework != ".NETStandard")
				return (info, false);
			var best = info;
			bool fromReference = false;
			Version? bestVersion = info.Framework == ".NETCoreApp" && Version.TryParse(info.Version, out var v) ? v : null;
			Version.TryParse(info.Version, out var netStandardVersion);
			var netStandardMoniker = info.GetTargetFrameworkMoniker();
			int bitness = module.GetPointerSize(IntPtr.Size) * 8;
			foreach (var asmRef in module.GetAssemblyRefs()) {
				TargetFrameworkInfo refInfo;
				// The framework's assemblies are replaced with the reference assemblies of the chosen framework, except
				// the ones .NET Standard doesn't have
				if (IsPublicKeyToken(contractsPublicKeyTokens, asmRef.PublicKeyOrToken?.Token)) {
					if (info.Framework != ".NETStandard" || netStandardVersion is null || netStandardMoniker is null || asmRef.Name == "netstandard" ||
						FindNetStandardReference(dotNetPathProvider, netStandardVersion, netStandardMoniker, asmRef.Name + ".dll", bitness) is not null)
						continue;
					// The package's .NET Standard build: compiled against .NET Standard like the original (with .NET's
					// reference assemblies the code could use APIs that .NET Framework doesn't have)
					if (FindNetStandardPackage(asmRef, netStandardMoniker) is not null)
						continue;
					var package = module.Context.AssemblyResolver.Resolve(asmRef, module)?.ManifestModule;
					var packageInfo = package is null ? (TargetFrameworkInfo?)null : TargetFrameworkInfo.Create(package);
					// Its .NET Standard build (next to the library): as before
					if (packageInfo?.Framework == ".NETStandard")
						continue;
					if (packageInfo?.Framework == ".NETCoreApp")
						refInfo = packageInfo.Value;
					else if (dotNetPathProvider.TryGetLatestNetStandardCompatibleVersion(netStandardVersion, out var latest) && latest is not null)
						refInfo = TargetFrameworkInfo.CreateDotNet(latest);
					else
						continue;
				}
				else {
					var resolved = module.Context.AssemblyResolver.Resolve(asmRef, module);
					if (resolved?.ManifestModule is not ModuleDef refModule)
						continue;
					refInfo = TargetFrameworkInfo.Create(refModule);
				}
				if (refInfo.Framework != ".NETCoreApp" || !Version.TryParse(refInfo.Version, out var refVersion))
					continue;
				if (bestVersion is null || refVersion > bestVersion) {
					best = refInfo;
					bestVersion = refVersion;
					fromReference = true;
				}
			}
			return (best, fromReference);
		}

		public IEnumerable<ModuleDef> Find(IEnumerable<string> extraAssemblyReferences) {
			Initialize(extraAssemblyReferences);
			yield return module;
			foreach (var asm in assemblies.Values) {
				foreach (var m in asm.Modules) {
					cancellationToken.ThrowIfCancellationRequested();
					yield return m;
				}
			}
		}

		void Initialize(IEnumerable<string> extraAssemblyReferences) {
			foreach (var asm in GetAssemblies(module, extraAssemblyReferences)) {
				cancellationToken.ThrowIfCancellationRequested();
				if (!assemblies.TryGetValue(asm, out var otherAsm))
					assemblies[asm] = asm;
				else if (asm.Version > otherAsm.Version)
					assemblies[asm] = asm;
			}
		}

		IEnumerable<AssemblyDef> GetAssemblies(ModuleDef module, IEnumerable<string> extraAssemblyReferences) {
			var asm = module.Assembly;
			if (asm is not null) {
				foreach (var a in GetAssemblies(asm))
					yield return a;
			}

			foreach (var asmRef in GetAssemblyRefs(module, extraAssemblyReferences)) {
				cancellationToken.ThrowIfCancellationRequested();
				asm = Resolve(module, asmRef);
				if (asm is null)
					continue;
				foreach (var a in GetAssemblies(asm))
					yield return a;
			}
		}

		IEnumerable<IAssembly> GetAssemblyRefs(ModuleDef module, IEnumerable<string> extraAssemblyReferences) {
			foreach (var a in module.GetAssemblyRefs())
				yield return a;
			foreach (var s in extraAssemblyReferences) {
				var info = new AssemblyNameInfo(s);
				if (info.Version is not null)
					yield return info;
			}
		}

		IEnumerable<AssemblyDef> GetAssemblies(AssemblyDef asm) {
			yield return asm;
			foreach (var m in asm.Modules) {
				cancellationToken.ThrowIfCancellationRequested();
				// Also include all contract assemblies since they have type forwarders
				// to eg. mscorlib.
				foreach (var a in GetResolvedContractAssemblies(m))
					yield return a;
			}
		}
		static readonly PublicKeyToken[] contractsPublicKeyTokens = new PublicKeyToken[] {
			// Normal contract asms
			new PublicKeyToken("b03f5f7f11d50a3a"),
			// netstandard
			new PublicKeyToken("cc7b13ffcd2ddd51"),
		};

		static bool IsPublicKeyToken(PublicKeyToken[] tokens, PublicKeyToken? token) {
			if (token is null)
				return false;
			foreach (var t in tokens) {
				if (token.Equals(t))
					return true;
			}
			return false;
		}

		static bool IsOtherReferenceAssembly(IAssembly assembly) {
			string name = assembly.Name;
			if (PublicKeyBase.IsNullOrEmpty2(assembly.PublicKeyOrToken)) {
				const string UnityEngine = "UnityEngine";
				if (StringComparer.OrdinalIgnoreCase.Equals(name, UnityEngine) || name.StartsWith(UnityEngine + ".", StringComparison.OrdinalIgnoreCase))
					return true;
			}
			return false;
		}

		IEnumerable<AssemblyDef> GetResolvedContractAssemblies(ModuleDef module) {
			var nonContractAsms = new HashSet<IAssembly>(AssemblyNameComparer.CompareAll);
			var stack = new Stack<AssemblyRef>(module.GetAssemblyRefs());
			while (stack.Count > 0) {
				cancellationToken.ThrowIfCancellationRequested();
				var asmRef = stack.Pop();
				if (!IsPublicKeyToken(contractsPublicKeyTokens, asmRef.PublicKeyOrToken?.Token) && !IsOtherReferenceAssembly(asmRef))
					continue;
				if (checkedContractsAssemblies.Contains(asmRef))
					continue;
				checkedContractsAssemblies.Add(asmRef);

				var contractsAsm = Resolve(module, asmRef);
				if (contractsAsm is not null) {
					yield return contractsAsm;
					foreach (var m in contractsAsm.Modules) {
						foreach (var ar in m.GetAssemblyRefs()) {
							cancellationToken.ThrowIfCancellationRequested();
							if (IsPublicKeyToken(contractsPublicKeyTokens, ar.PublicKeyOrToken?.Token) || IsOtherReferenceAssembly(ar))
								stack.Push(ar);
							else
								nonContractAsms.Add(ar);
						}
					}
				}
			}
			foreach (var asmRef in nonContractAsms) {
				cancellationToken.ThrowIfCancellationRequested();
				var asm = Resolve(module, asmRef);
				if (asm is not null)
					yield return asm;
			}
		}

		static readonly Version ver2_0_0_0 = new Version(2, 0, 0, 0);

		/// <summary>
		/// The .NET Standard build of a package that isn't part of .NET Standard (e.g. System.Collections.Immutable) in the
		/// NuGet package folders: the lowest version that has the assembly version, or null. (netstandard2.1: also its
		/// netstandard2.0 build.)
		/// </summary>
		static string? FindNetStandardPackage(IAssembly asmRef, string moniker) {
			var monikers = moniker == "netstandard2.1" ? new[] { moniker, "netstandard2.0" } : new[] { moniker };
			var fileName = asmRef.Name + ".dll";
			foreach (var source in NuGetPackageLocator.GetPossibleNuGetPackageLocations(asmRef.Name.String.ToLowerInvariant(), null)) {
				string? best = null;
				Version? bestVersion = null;
				foreach (var versionPath in source) {
					foreach (var m in monikers) {
						var file = Path.Combine(versionPath, "lib", m, fileName);
						if (!File.Exists(file))
							continue;
						Version? version;
						try {
							using var package = ModuleDefMD.Load(file);
							version = package.Assembly?.Version;
						}
						catch (BadImageFormatException) {
							continue;
						}
						catch (IOException) {
							continue;
						}
						if (version is null || version < asmRef.Version || (bestVersion is not null && version >= bestVersion))
							break;
						best = file;
						bestVersion = version;
						break;
					}
				}
				if (best is not null)
					return best;
			}
			return null;
		}

		/// <summary>
		/// Is a Microsoft.NETCore.App X.Y.* (the version's major and minor) installed?
		/// </summary>
		bool IsRuntimeInstalled(Version version, int bitness) {
			foreach (var path in dotNetPathProvider.TryGetSharedDotNetPaths(version, bitness) ?? Array.Empty<string>()) {
				if (Version.TryParse(Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)).Split('-')[0], out var installed) &&
					installed.Major == version.Major && installed.Minor == version.Minor)
					return true;
			}
			return false;
		}

		/// <summary>
		/// The 'ref\netX.Y' directory of a reference pack, if it has one
		/// </summary>
		static string? GetOnlyReferenceDirectory(string packPath) {
			try {
				var dirs = Directory.GetDirectories(Path.Combine(packPath, "ref"), "net*");
				return dirs.Length == 1 ? dirs[0] : null;
			}
			catch (IOException) {
				return null;
			}
			catch (UnauthorizedAccessException) {
				return null;
			}
		}

		/// <summary>
		/// .NET Standard's reference assembly (netstandard2.1: the SDK's, else the NETStandard.Library package's), or null
		/// if it's not part of .NET Standard
		/// </summary>
		static string? FindNetStandardReference(DotNetPathProvider dotNetPathProvider, Version ver, string moniker, string fileName, int bitness) {
			if (ver > ver2_0_0_0) {
				foreach (string path in dotNetPathProvider.TryGetNetStandardReferencePaths(ver, bitness) ?? Array.Empty<string>()) {
					var newFileName = Path.Combine(path, "ref", moniker, fileName);
					if (File.Exists(newFileName))
						return newFileName;
				}
				return null;
			}

			foreach (var source in NuGetPackageLocator.GetPossibleNuGetPackageLocations("netstandard.library", null)) {
				string? targetPath = null;
				Version? maxCompatibleVersion = null;
				foreach (string versionPath in source) {
					var versionText = Path.GetFileName(versionPath);
					if (!Version.TryParse(versionText, out var packageVer))
						continue;

					if (packageVer.Major != ver.Major || packageVer.Minor != ver.Minor)
						continue;

					if (maxCompatibleVersion is not null && packageVer <= maxCompatibleVersion)
						continue;

					var newPath = Path.Combine(versionPath, "build", moniker, "ref", fileName);
					if (!File.Exists(newPath))
						continue;

					maxCompatibleVersion = packageVer;
					targetPath = newPath;
				}

				if (targetPath is not null)
					return targetPath;
			}
			return null;
		}

		AssemblyDef? Resolve(ModuleDef module, IAssembly asmRef) {
			if (referenceAssemblies.TryGetValue(asmRef, out var refAsm))
				return refAsm;
			var resolved = module.Context.AssemblyResolver.Resolve(asmRef, module);
			// A package of a .NET Standard library that's part of .NET (GetCompileTarget) isn't always found (e.g. in the
			// GAC): it's in the .NET it's compiled against
			// (Or in a NuGet package folder: FindNetStandardPackage)
			bool fromTarget = resolved is null && ((targetFromReference && targetFrameworkInfo.Framework == ".NETCoreApp") || targetFrameworkInfo.Framework == ".NETStandard");
			if (resolved is null && !fromTarget)
				return null;
			if (!dotNetPathProvider.HasDotNet || targetFrameworkInfo.IsDotNetFramework)
				return resolved;
			if (!IsPublicKeyToken(contractsPublicKeyTokens, asmRef.PublicKeyOrToken?.Token))
				return resolved;

			if (!Version.TryParse(targetFrameworkInfo.Version, out var ver))
				return resolved;
			var moniker = targetFrameworkInfo.GetTargetFrameworkMoniker();
			if (moniker is null)
				return resolved;

			var fileName = resolved is null ? asmRef.Name + ".dll" : Path.GetFileName(resolved.ManifestModule.Location);

			string[]? referencePaths;
			switch (targetFrameworkInfo.Framework) {
			case ".NETStandard":
				var targetPath = FindNetStandardReference(dotNetPathProvider, ver, moniker, fileName, module.GetPointerSize(IntPtr.Size) * 8) ??
					// Not part of .NET Standard, e.g. System.Collections.Immutable: its package's .NET Standard build (the
					// runtime assembly of the GAC or of .NET has another core library: CS0518, CS0433)
					(asmRef.Name != "netstandard" ? FindNetStandardPackage(asmRef, moniker) : null);
				if (targetPath is null)
					return resolved;

				var newModule = ModuleDefMD.Load(targetPath, module.Context);
				(newModule.Metadata.PEImage as IInternalPEImage)?.UnsafeDisableMemoryMappedIO();
				return referenceAssemblies[asmRef] = newModule.Assembly;
			case ".NETCoreApp": {
				referencePaths = dotNetPathProvider.TryGetReferenceDotNetPaths(ver, module.GetPointerSize(IntPtr.Size) * 8);
				break;
			}
			default:
				return resolved;
			}

			foreach (string path in referencePaths ?? Array.Empty<string>()) {
				var newFileName = Path.Combine(path, "ref", moniker, fileName);
				// Neither the version's reference pack nor its runtime is installed (e.g. .NET 5): it rolls forward to a newer
				// .NET, so the reference assemblies of that pack, all of them. (The resolver's runtime assemblies were a mix:
				// .NET 6's System.Runtime.CompilerServices.Unsafe with Unsafe and .NET 10's System.Private.CoreLib with
				// Unsafe, CS0433.) With its runtime, the runtime's assemblies are used as before: a newer pack has APIs the
				// version doesn't have.
				if (targetFrameworkInfo.Framework == ".NETCoreApp" && !Directory.Exists(Path.Combine(path, "ref", moniker)) &&
					!IsRuntimeInstalled(ver, module.GetPointerSize(IntPtr.Size) * 8) && GetOnlyReferenceDirectory(path) is string otherDir)
					newFileName = Path.Combine(otherDir, fileName);
				if (!File.Exists(newFileName))
					continue;
				var newModule = ModuleDefMD.Load(newFileName, module.Context);
				(newModule.Metadata.PEImage as IInternalPEImage)?.UnsafeDisableMemoryMappedIO();
				return referenceAssemblies[asmRef] = newModule.Assembly;
			}

			// No reference assemblies (no SDK): the resolved one (if any) is the module's framework's, use the newer
			// .NET's runtime assembly instead
			if (targetFromReference && targetFrameworkInfo.Framework == ".NETCoreApp") {
				foreach (string path in dotNetPathProvider.TryGetSharedDotNetPaths(ver, module.GetPointerSize(IntPtr.Size) * 8) ?? Array.Empty<string>()) {
					var newFileName = Path.Combine(path, fileName);
					if (!File.Exists(newFileName))
						continue;
					var newModule = ModuleDefMD.Load(newFileName, module.Context);
					(newModule.Metadata.PEImage as IInternalPEImage)?.UnsafeDisableMemoryMappedIO();
					return referenceAssemblies[asmRef] = newModule.Assembly;
				}
			}

			return resolved;
		}
	}
}
