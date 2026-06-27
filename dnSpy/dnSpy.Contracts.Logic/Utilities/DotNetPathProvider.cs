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
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace dnSpy.Contracts.Utilities {
	/// <summary>
	/// Locates .NET Core installation directories
	/// </summary>
	public sealed class DotNetPathProvider {
		readonly FrameworkPaths[] netPathsShared;
		readonly FrameworkPaths[] netPathsRef;
		readonly FrameworkPaths[] netStandardPaths;

		/// <summary>
		/// Returns true if .NET Core was found on the system, false otherwise.
		/// </summary>
		public bool HasDotNet => netPathsShared.Length != 0;

		readonly struct DotNetPathInfo {
			public readonly string Directory;
			public readonly int Bitness;
			public DotNetPathInfo(string directory, int bitness) {
				Directory = directory ?? throw new ArgumentNullException(nameof(directory));
				Bitness = bitness;
			}
		}

		// It treats all previews/alphas as the same version. This is needed because .NET Core 3.0 preview's
		// shared frameworks don't have the same version numbers, eg.:
		//		shared\Microsoft.AspNetCore.App\3.0.0-preview-18579-0056
		//		shared\Microsoft.NETCore.App\3.0.0-preview-27216-02
		//		shared\Microsoft.WindowsDesktop.App\3.0.0-alpha-27214-12
		readonly struct FrameworkVersionIgnoreExtra : IEquatable<FrameworkVersionIgnoreExtra> {
			readonly FrameworkVersion version;

			public FrameworkVersionIgnoreExtra(FrameworkVersion version) => this.version = version;

			public bool Equals(FrameworkVersionIgnoreExtra other) =>
				version.Major == other.version.Major &&
				version.Minor == other.version.Minor &&
				version.Patch == other.version.Patch &&
				(version.Extra.Length == 0) == (other.version.Extra.Length == 0);

			public override bool Equals(object? obj) => obj is FrameworkVersionIgnoreExtra other && Equals(other);
			public override int GetHashCode() => version.Major ^ version.Minor ^ version.Patch ^ (version.Extra.Length == 0 ? 0 : -1);
		}

		/// <summary>
		/// Constructor
		/// </summary>
		public DotNetPathProvider() {
			var sharedPaths = new List<FrameworkPath>();
			var refPaths = new List<FrameworkPath>();
			var standardPaths = new List<FrameworkPath>();
			foreach (var info in GetDotNetBaseDirs()) {
				sharedPaths.AddRange(GetDotNetPathsShared(info.Directory, info.Bitness));
				refPaths.AddRange(GetDotNetPathsRef(info.Directory, info.Bitness));
				standardPaths.AddRange(GetNetStandardRefPaths(info.Directory, info.Bitness));
			}

			netPathsShared = CreateAndSortFrameworkPaths(sharedPaths, false);
			netPathsRef = CreateAndSortFrameworkPaths(refPaths, true);
			netStandardPaths = CreateAndSortFrameworkPaths(standardPaths, true);
		}

		static FrameworkPaths[] CreateAndSortFrameworkPaths(IEnumerable<FrameworkPath> list, bool isRef) {
			var paths = from p in list
						group p by new { Path = (Path.GetDirectoryName(Path.GetDirectoryName(p.Path)) ?? string.Empty).ToUpperInvariant(), p.Bitness, Version = new FrameworkVersionIgnoreExtra(p.Version) } into g
						where !string.IsNullOrEmpty(g.Key.Path)
						select new FrameworkPaths(g.ToArray(), isRef);
			var array = paths.ToArray();
			Array.Sort(array);
			return array;
		}

		/// <summary>
		/// Returns the .NET Core runtime assembly directories found on the system.
		/// </summary>
		/// <returns>.NET Core framework path info</returns>
		public FrameworkPath[] GetSharedDotNetPaths() => netPathsShared.SelectMany(x => x.frameworkPaths).ToArray();

		/// <summary>
		/// Returns the .NET Core runtime assembly directories found on the system.
		/// </summary>
		/// <param name="version">Preferred .NET Core version</param>
		/// <param name="bitness">Preferred bitness</param>
		/// <returns>.NET Core runtime assembly directories, null if not found</returns>
		public string[]? TryGetSharedDotNetPaths(Version version, int bitness) => TryGetBestMatchingPaths(version, bitness, netPathsShared);

		/// <summary>
		/// Returns the .NET Core reference assembly directories found on the system.
		/// </summary>
		/// <param name="version">Preferred .NET Core version</param>
		/// <param name="bitness">Preferred bitness</param>
		/// <returns>.NET Core reference assembly directories, null if not found</returns>
		public string[]? TryGetReferenceDotNetPaths(Version version, int bitness) => TryGetBestMatchingPaths(version, bitness, netPathsRef);

		/// <summary>
		/// Returns the .NET Standard reference assembly directories found on the system.
		/// </summary>
		/// <param name="version">Preferred .NET Standard version</param>
		/// <param name="bitness">Preferred bitness</param>
		/// <returns> .NET Standard reference assembly directories, null if not found</returns>
		public string[]? TryGetNetStandardReferencePaths(Version version, int bitness) => TryGetBestMatchingPaths(version, bitness, netStandardPaths);

		static string[]? TryGetBestMatchingPaths(Version version, int bitness, FrameworkPaths[] paths) {
			Debug.Assert(bitness == 32 || bitness == 64);
			int bitness2 = bitness ^ 0x60;

			var info = TryGetDotNetPathsCore(version.Major, version.Minor, bitness, paths) ??
					   TryGetDotNetPathsCore(version.Major, version.Minor, bitness2, paths);
			if (info is not null)
				return info.Paths;

			info = TryGetDotNetPathsCore(version.Major, bitness, paths) ??
				   TryGetDotNetPathsCore(version.Major, bitness2, paths);
			if (info is not null)
				return info.Paths;

			info = TryGetDotNetPathsCore(bitness, paths) ??
				   TryGetDotNetPathsCore(bitness2, paths);
			return info?.Paths;
		}

		static FrameworkPaths? TryGetDotNetPathsCore(int major, int minor, int bitness, FrameworkPaths[] pathsArray) {
			FrameworkPaths? fpMajor = null;
			FrameworkPaths? fpMajorMinor = null;
			for (int i = pathsArray.Length - 1; i >= 0; i--) {
				var info = pathsArray[i];
				if (info.Bitness == bitness && info.Version.Major == major) {
					if (fpMajor is null)
						fpMajor = info;
					else
						fpMajor = BestMinorVersion(minor, fpMajor, info);
					if (info.Version.Minor == minor) {
						if (info.HasDotNetAppPath)
							return info;
						fpMajorMinor ??= info;
					}
				}
			}
			return fpMajorMinor ?? fpMajor;
		}

		/// <summary>
		/// Returns the .NET runtime assembly directories on the system which exactly match the search criteria.
		/// </summary>
		/// <param name="version">The exact version of the .NET runtime</param>
		/// <param name="bitness">The bitness of the .NET runtime</param>
		/// <returns>The .NET runtime assembly directories. Empty if non where found.</returns>
		public string[] TryFindExactRuntimePaths(Version version, int bitness) {
			var candidates = new List<string>();
			foreach (var frameworkPaths in netPathsShared) {
				if (frameworkPaths.Bitness != bitness)
					continue;
				if (frameworkPaths.SystemVersion == version)
					candidates.AddRange(frameworkPaths.Paths);
			}
			return candidates.ToArray();
		}

		static FrameworkPaths BestMinorVersion(int minor, FrameworkPaths a, FrameworkPaths b) {
			uint da = VerDist(minor, a.Version.Minor);
			uint db = VerDist(minor, b.Version.Minor);
			if (da < db)
				return a;
			if (db < da)
				return b;
			if (!string.IsNullOrEmpty(b.Version.Extra))
				return a;
			return string.IsNullOrEmpty(a.Version.Extra) ? a : b;
		}

		// Any ver < minVer is worse than any ver >= minVer
		static uint VerDist(int minVer, int ver) {
			if (ver >= minVer)
				return (uint)(ver - minVer);
			return 0x80000000 + (uint)minVer - (uint)ver - 1;
		}

		static FrameworkPaths? TryGetDotNetPathsCore(int major, int bitness, FrameworkPaths[] pathsArray) {
			FrameworkPaths? fpMajor = null;
			for (int i = pathsArray.Length - 1; i >= 0; i--) {
				var info = pathsArray[i];
				if (info.Bitness == bitness && info.Version.Major == major) {
					if (info.HasDotNetAppPath)
						return info;
					fpMajor ??= info;
				}
			}
			return fpMajor;
		}

		static FrameworkPaths? TryGetDotNetPathsCore(int bitness, FrameworkPaths[] pathsArray) {
			FrameworkPaths? best = null;
			for (int i = pathsArray.Length - 1; i >= 0; i--) {
				var info = pathsArray[i];
				if (info.Bitness == bitness) {
					if (info.HasDotNetAppPath)
						return info;
					best ??= info;
				}
			}
			return best;
		}

		static readonly string DotNetExeName = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "dotnet.exe" : "dotnet";
		static readonly Regex NetCoreRuntimePattern = new Regex(@"\.NET( Core)? \d+\.\d+\.\d+");

		static IEnumerable<DotNetPathInfo> GetDotNetBaseDirs() {
			var hash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			foreach (var tmp in GetDotNetBaseDirCandidates()) {
				var path = tmp.Trim();
				if (!Directory.Exists(path))
					continue;
				try {
					path = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(path));
				}
				catch (ArgumentException) {
					continue;
				}
				catch (PathTooLongException) {
					continue;
				}
				if (!hash.Add(path))
					continue;
				string file;
				try {
					file = Path.Combine(path, DotNetExeName);
				}
				catch {
					continue;
				}
				if (!File.Exists(file))
					continue;
				int bitness;
				try {
					bitness = GetPeFileBitness(file);
				}
				catch {
					continue;
				}
				if (bitness == -1)
					continue;
				yield return new DotNetPathInfo(path, bitness);
			}
		}

		// NOTE: This same method exists in DotNetHelpers (CorDebug project). Update both methods if this one gets updated.
		static IEnumerable<string> GetDotNetBaseDirCandidates() {
			if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) {
				// Microsoft tools don't check the PATH env var, only the default locations (eg. ProgramFiles)
				var envVars = new[] {
					"PATH",
					"DOTNET_ROOT(x86)",
					"DOTNET_ROOT",
				};
				foreach (var envVar in envVars) {
					var pathEnvVar = Environment.GetEnvironmentVariable(envVar) ?? string.Empty;
					foreach (var path in pathEnvVar.Split(new[] { Path.PathSeparator }, StringSplitOptions.RemoveEmptyEntries))
						yield return path;
				}

				var regPathFormat = IntPtr.Size == 4 ?
					@"SOFTWARE\dotnet\Setup\InstalledVersions\{0}" :
					@"SOFTWARE\WOW6432Node\dotnet\Setup\InstalledVersions\{0}";
				var archs = new[] { "x86", "x64" };
				foreach (var arch in archs) {
					var regPath = string.Format(regPathFormat, arch);
					if (TryGetInstallLocationFromRegistry(regPath, out var installLocation))
						yield return installLocation;
				}

				// Check default locations
				var progDirX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
				var progDir = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
				if (!string.IsNullOrEmpty(progDirX86) && StringComparer.OrdinalIgnoreCase.Equals(progDirX86, progDir) && Path.GetDirectoryName(progDir) is string baseDir)
					progDir = Path.Combine(baseDir, "Program Files");
				const string dotnetDirName = "dotnet";
				if (!string.IsNullOrEmpty(progDir))
					yield return Path.Combine(progDir, dotnetDirName);
				if (!string.IsNullOrEmpty(progDirX86))
					yield return Path.Combine(progDirX86, dotnetDirName);
			}
			else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) {
				yield return "/usr/share/dotnet/";
				yield return "/usr/local/share/dotnet/";
				yield return "/opt/dotnet/";
			}

			if (NetCoreRuntimePattern.Match(RuntimeInformation.FrameworkDescription).Success) {
				// Fallback: if we are currently running .NET Core or newer, we can infer the installation directory
				// with the help of System.Reflection. The assembly of System.Object is either System.Runtime
				// or System.Private.CoreLib, which is located at <installation_directory>/shared/<runtime>/<version>/.

				string corlibPath = typeof(object).Assembly.Location;
				string? versionPath = Path.GetDirectoryName(corlibPath);
				string? runtimePath = Path.GetDirectoryName(versionPath);
				string? sharedPath = Path.GetDirectoryName(runtimePath);
				if (Path.GetDirectoryName(sharedPath) is string dir)
					yield return dir;
			}
		}

		static bool TryGetInstallLocationFromRegistry(string regPath, [NotNullWhen(true)] out string? installLocation) {
			using (var key = Registry.LocalMachine.OpenSubKey(regPath)) {
				installLocation = key?.GetValue("InstallLocation") as string;
				return installLocation is not null;
			}
		}

		static IEnumerable<FrameworkPath> GetDotNetPathsShared(string basePath, int bitness) {
			if (!Directory.Exists(basePath))
				yield break;
			var sharedDir = Path.Combine(basePath, "shared");
			if (!Directory.Exists(sharedDir))
				yield break;
			// Known dirs: Microsoft.NETCore.App, Microsoft.WindowsDesktop.App, Microsoft.AspNetCore.All, Microsoft.AspNetCore.App
			foreach (var versionsDir in GetDirectories(sharedDir)) {
				foreach (var dir in GetDirectories(versionsDir)) {
					var frameWorkPath = CreateFrameworkPath(dir, bitness);
					if (frameWorkPath is not null)
						yield return frameWorkPath;
				}
			}
		}

		static IEnumerable<FrameworkPath> GetDotNetPathsRef(string basePath, int bitness) {
			if (!Directory.Exists(basePath))
				yield break;
			var packsDir = Path.Combine(basePath, "packs");
			if (!Directory.Exists(packsDir))
				yield break;
			// Known dirs: Microsoft.NETCore.App.Ref, Microsoft.WindowsDesktop.App.Ref, Microsoft.AspNetCore.App.Ref
			foreach (var packDirs in GetDirectories(packsDir)) {
				// Exclude app host packs and .NET Standard packs
				if (!packDirs.EndsWith(".Ref", StringComparison.Ordinal) || packDirs.EndsWith(NetStandardRefPackName, StringComparison.Ordinal))
					continue;
				foreach (var versionDir in GetDirectories(packDirs)) {
					var frameWorkPath = CreateFrameworkPath(versionDir, bitness);
					if (frameWorkPath is not null)
						yield return frameWorkPath;
				}
			}
		}

		const string NetStandardRefPackName = "NETStandard.Library.Ref";

		static IEnumerable<FrameworkPath> GetNetStandardRefPaths(string basePath, int bitness) {
			if (!Directory.Exists(basePath))
				yield break;
			var packsDir = Path.Combine(basePath, "packs");
			if (!Directory.Exists(packsDir))
				yield break;
			var netStandardPack = Path.Combine(packsDir, NetStandardRefPackName);
			if (!Directory.Exists(netStandardPack))
				yield break;
			foreach (string directory in GetDirectories(netStandardPack)) {
				var frameWorkPath = CreateFrameworkPath(directory, bitness);
				if (frameWorkPath is not null)
					yield return frameWorkPath;
			}
		}

		static FrameworkPath? CreateFrameworkPath(string dir, int bitness) {
			var name = Path.GetFileName(dir);
			var m = Regex.Match(name, @"^(\d+)\.(\d+)\.(\d+)$");
			if (m.Groups.Count == 4) {
				var g = m.Groups;
				return new FrameworkPath(dir, bitness, ToFrameworkVersion(g[1].Value, g[2].Value, g[3].Value, string.Empty));
			}

			m = Regex.Match(name, @"^(\d+)\.(\d+)\.(\d+)-(.*)$");
			if (m.Groups.Count == 5) {
				var g = m.Groups;
				return new FrameworkPath(dir, bitness, ToFrameworkVersion(g[1].Value, g[2].Value, g[3].Value, g[4].Value));
			}

			return null;
		}

		static int ParseInt32(string s) => int.TryParse(s, out var res) ? res : 0;
		static FrameworkVersion ToFrameworkVersion(string a, string b, string c, string d) =>
			new FrameworkVersion(ParseInt32(a), ParseInt32(b), ParseInt32(c), d);

		static string[] GetDirectories(string dir) {
			try {
				return Directory.GetDirectories(dir);
			}
			catch {
			}
			return Array.Empty<string>();
		}

		static int GetPeFileBitness(string file) {
			using (var f = File.OpenRead(file)) {
				var r = new BinaryReader(f);
				if (r.ReadUInt16() != 0x5A4D)
					return -1;
				f.Position = 0x3C;
				f.Position = r.ReadUInt32();
				if (r.ReadUInt32() != 0x4550)
					return -1;
				f.Position += 0x14;
				ushort magic = r.ReadUInt16();
				return magic switch {
					0x10B => 32,
					0x20B => 64,
					_ => -1
				};
			}
		}

		/// <summary>
		/// Gets the .NET Core version of the specified runtime assembly.
		/// </summary>
		/// <param name="filename">Path to the runtime assembly</param>
		/// <returns>.NET Core version, null if not found</returns>
		public Version? TryGetDotNetVersion(string filename) {
			foreach (var info in netPathsShared) {
				foreach (var path in info.Paths) {
					if (IsFileInDir(path, filename))
						return info.SystemVersion;
				}
			}

			return null;
		}

		/// <summary>
		/// Locates the assembly name in the .NET Core runtime directories.
		/// </summary>
		/// <param name="assemblyName">The assembly name to look up</param>
		/// <param name="runtimeVersion">The version of the runtime</param>
		/// <param name="bitness">The bitness of the runtime</param>
		/// <param name="runtimePack">The runtime pack which contained the assembly</param>
		/// <returns>Full path to the assembly</returns>
		public bool TryGetRuntimePackOfAssembly(string assemblyName, Version runtimeVersion, int bitness, [NotNullWhen(true)] out string? runtimePack) {
			string[]? dotNetPaths = TryGetSharedDotNetPaths(runtimeVersion, bitness);
			if (dotNetPaths is not null) {
				foreach (string path in dotNetPaths) {
					string assemblyPath = Path.Combine(path, assemblyName + ".dll");
					if (File.Exists(assemblyPath)) {
						runtimePack = Path.GetFileName(Path.GetDirectoryName(path)!);
						return true;
					}

					assemblyPath = Path.Combine(path, assemblyName + ".exe");
					if (File.Exists(assemblyPath)) {
						runtimePack = Path.GetFileName(Path.GetDirectoryName(path)!);
						return true;
					}
				}
			}

			runtimePack = null;
			return false;
		}

		static bool IsFileInDir(string dir, string file) {
			Debug.Assert(dir.Length != 0);
			if (dir.Length >= file.Length)
				return false;
			var c = file[dir.Length];
			if (c != Path.DirectorySeparatorChar && c != Path.AltDirectorySeparatorChar)
				return false;
			if (!file.StartsWith(dir, StringComparison.OrdinalIgnoreCase))
				return false;
			return file.IndexOfAny(dirSeps, dir.Length + 1) < 0;
		}

		static readonly char[] dirSeps = Path.DirectorySeparatorChar == Path.AltDirectorySeparatorChar ?
			new[] { Path.DirectorySeparatorChar } :
			new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };


		/// <summary>
		/// Gets the latest compatible .NET Core version for the specified .NET Standard version
		/// </summary>
		/// <param name="netStandardVersion">.NET Standard version</param>
		/// <param name="netCoreVersion">Latest compatible .NET Core version</param>
		/// <returns>false if a compatible .NET Standard version could not be found, false otherwise.</returns>
		public bool TryGetLatestNetStandardCompatibleVersion(Version netStandardVersion, out Version? netCoreVersion) {
			netCoreVersion = null;

			if (!HasDotNet)
				return false;

			bool foundMatch = false;
			foreach (var info in netPathsShared) {
				if (info.IsCompatibleWithNetStandard(netStandardVersion) &&
					(netCoreVersion is null || info.SystemVersion > netCoreVersion)) {
					foundMatch = true;
					netCoreVersion = info.SystemVersion;
				}
			}

			return foundMatch;
		}

		internal bool TryGetClosestNetStandardCompatibleVersion(Version netStandardVersion, out Version? netCoreVersion) {
			netCoreVersion = null;

			if (!HasDotNet)
				return false;

			var version2 = new Version(2, 0, 0, 0);
			FrameworkVersion maxNetCoreVersion;
			if (netStandardVersion < version2)
				maxNetCoreVersion = new FrameworkVersion(1, 1, 0, "");
			else if (netStandardVersion == version2)
				maxNetCoreVersion = new FrameworkVersion(2, 2, 0, "");
			else
				return TryGetLatestNetStandardCompatibleVersion(netStandardVersion, out netCoreVersion);

			Version? max = null;
			foreach (var info in netPathsShared) {
				if (info.Version.CompareTo(maxNetCoreVersion) < 0 &&
					info.IsCompatibleWithNetStandard(netStandardVersion) && (max is null || info.SystemVersion > max))
					max = info.SystemVersion;
			}

			if (max is not null) {
				netCoreVersion = max;
				return true;
			}

			return false;
		}
	}
}
