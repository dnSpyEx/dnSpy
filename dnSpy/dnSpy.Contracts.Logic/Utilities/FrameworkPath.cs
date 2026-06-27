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
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace dnSpy.Contracts.Utilities {
	// It's a class since very few of these are created
	[DebuggerDisplay("{Bitness,d}-bit {Version,nq} {DebuggerPaths,nq}")]
	sealed class FrameworkPaths : IComparable<FrameworkPaths> {
		public readonly string[] Paths;
		public readonly int Bitness;
		public readonly FrameworkVersion Version;
		public readonly Version SystemVersion;
		public readonly bool IsReferencePath;
		internal readonly FrameworkPath[] frameworkPaths;

		string DebuggerPaths => string.Join(Path.PathSeparator.ToString(), Paths);

		public FrameworkPaths(FrameworkPath[] paths, bool isRef) {
			frameworkPaths = paths;
			var firstPath = paths[0];
#if DEBUG
			for (int i = 1; i < paths.Length; i++) {
				if (firstPath.Bitness != paths[i].Bitness)
					throw new ArgumentException();
				// Ignore Extra since it can be different if it's a preview
				if (firstPath.Version.Major != paths[i].Version.Major ||
					firstPath.Version.Minor != paths[i].Version.Minor ||
					firstPath.Version.Patch != paths[i].Version.Patch)
					throw new ArgumentException();
			}
#endif
			IsReferencePath = isRef;
			var allPaths = paths.Select(a => a.Path).ToArray();
			Array.Sort(allPaths, SortPaths);
			Paths = allPaths;
			Bitness = firstPath.Bitness;
			Version = firstPath.Version;
			SystemVersion = new Version(firstPath.Version.Major, firstPath.Version.Minor, firstPath.Version.Patch, 0);

			foreach (var p in Paths) {
				if (StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(Path.GetDirectoryName(p)), IsReferencePath ? DotNetAppRefDir : DotNetAppDir)) {
					HasDotNetAppPath = true;
					break;
				}
			}
		}

		// Sort the .NET dir last since it also contains some assemblies that exist in some other
		// dirs, eg. WindowsBase.dll is in both Microsoft.NETCore.App and Microsoft.WindowsDesktop.App
		// and the one in Microsoft.NETCore.App isn't the same one WPF apps expect (it has no types).
		// There are other dupe assemblies, eg. Microsoft.Win32.Registry.dll exists both in
		// Microsoft.NETCore.App and Microsoft.WindowsDesktop.App.
		const string DotNetAppDir = "Microsoft.NETCore.App";
		const string DotNetAppRefDir = "Microsoft.NETCore.App.Ref";
		int SortPaths(string x, string y) {
			int c = GetPathGroupOrder(x) - GetPathGroupOrder(y);
			return c != 0 ? c : StringComparer.OrdinalIgnoreCase.Compare(x, y);
		}

		int GetPathGroupOrder(string path) =>
			StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(Path.GetDirectoryName(path)),
				IsReferencePath ? DotNetAppRefDir : DotNetAppDir)
				? int.MaxValue
				: 0;

		public int CompareTo(FrameworkPaths? other) {
			if (other is null)
				return 1;
			int c = Version.CompareTo(other.Version);
			if (c != 0)
				return c;
			c = Bitness - other.Bitness;
			if (c != 0)
				return c;

			return CompareTo(Paths, other.Paths);
		}

		static int CompareTo(string[] a, string[] b) {
			if (a.Length != b.Length)
				return a.Length - b.Length;
			for (int i = 0; i < a.Length; i++) {
				int c = StringComparer.OrdinalIgnoreCase.Compare(Path.GetDirectoryName(a[i]), Path.GetDirectoryName(b[i]));
				if (c != 0)
					return c;
			}
			return 0;
		}

		internal bool HasDotNetAppPath { get; }

		public bool IsCompatibleWithNetStandard(Version netStandardVersion) {
			// All .NET Core versions are compatible with .NET Standard versions under 2.0
			if (netStandardVersion.Major != 2)
				return true;

			// .NET Standard 2.0 is compatible with .NET Core 2.0 and later
			// .NET Standard 2.1 is compatible with .NET Core 3.0 and later
			return netStandardVersion.Minor switch {
				0 => SystemVersion.Major >= 2,
				1 => SystemVersion.Major >= 3,
				_ => true
			};
		}
	}

	/// <summary>
	/// .NET Core path info
	/// </summary>
	// It's a class since very few of these are created
	[DebuggerDisplay("{Bitness,d}-bit {Version,nq} {Path,nq}")]
	public sealed class FrameworkPath {
		/// <summary>
		/// .NET Core assembly directories
		/// </summary>
		public readonly string Path;
		/// <summary>
		/// Assembly bitness
		/// </summary>
		public readonly int Bitness;
		/// <summary>
		/// Assembly version
		/// </summary>
		public readonly FrameworkVersion Version;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="path">.NET Core assembly directories</param>
		/// <param name="bitness">Assembly bitness</param>
		/// <param name="version">Assembly version</param>
		/// <exception cref="ArgumentNullException"><paramref name="path"/> is null</exception>
		public FrameworkPath(string path, int bitness, FrameworkVersion version) {
			Path = path ?? throw new ArgumentNullException(nameof(path));
			Bitness = bitness;
			Version = version;
		}
	}

	/// <summary>
	/// .NET Core version info
	/// </summary>
	public readonly struct FrameworkVersion : IComparable<FrameworkVersion>, IEquatable<FrameworkVersion> {
		/// <summary>
		/// The major component
		/// </summary>
		public readonly int Major;
		/// <summary>
		/// The minor component
		/// </summary>
		public readonly int Minor;
		/// <summary>
		/// The patch component
		/// </summary>
		public readonly int Patch;
		/// <summary>
		/// The extra component
		/// </summary>
		public readonly string Extra;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="major">The major component</param>
		/// <param name="minor">The minor component</param>
		/// <param name="patch">The patch component</param>
		/// <param name="extra">The extra component</param>
		public FrameworkVersion(int major, int minor, int patch, string extra) {
			Major = major;
			Minor = minor;
			Patch = patch;
			Extra = extra;
		}

		/// <inheritdoc/>
		public override string ToString() {
			if (Extra.Length == 0)
				return $"{Major}.{Minor}.{Patch}";
			return $"{Major}.{Minor}.{Patch}-{Extra}";
		}

		/// <inheritdoc/>
		public int CompareTo(FrameworkVersion other) {
			int c = Major.CompareTo(other.Major);
			if (c != 0)
				return c;
			c = Minor.CompareTo(other.Minor);
			if (c != 0)
				return c;
			c = Patch.CompareTo(other.Patch);
			if (c != 0)
				return c;
			return CompareExtra(Extra, other.Extra);
		}

		static int CompareExtra(string a, string b) {
			if (a.Length == 0 && b.Length == 0)
				return 0;
			if (a.Length == 0)
				return 1;
			if (b.Length == 0)
				return -1;
			return StringComparer.Ordinal.Compare(a, b);
		}

		/// <inheritdoc/>
		public bool Equals(FrameworkVersion other) =>
			Major == other.Major &&
			Minor == other.Minor &&
			Patch == other.Patch &&
			StringComparer.Ordinal.Equals(Extra, other.Extra);

		/// <inheritdoc/>
		public override bool Equals(object? obj) => obj is FrameworkVersion other && Equals(other);

		/// <inheritdoc/>
		public override int GetHashCode() => Major ^ Minor ^ Patch ^ StringComparer.Ordinal.GetHashCode(Extra ?? string.Empty);
	}
}
