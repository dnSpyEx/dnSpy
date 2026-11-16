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
using dnSpy.Contracts.App;

namespace dnSpy.Extension {
	/// <summary>
	/// Finds extension files and keeps track of the disabled ones. The list of disabled
	/// extensions must be available before the extensions are loaded, which happens before
	/// the MEF container is created, so it's stored in a text file next to the settings file.
	/// </summary>
	static class ExtensionFiles {
		const string ExtensionSearchPattern = "*.x.dll";
		const string ExtensionsSubDir = "Extensions";
		const string DisabledExtensionsFilenameName = "disabled-extensions.txt";

		/// <summary>
		/// File that contains the file names of the disabled extensions, one per line
		/// </summary>
		public static string DisabledExtensionsFilename {
			get {
				var settingsDir = Path.GetDirectoryName(AppDirectories.SettingsFilename);
				if (!string.IsNullOrEmpty(settingsDir))
					return Path.Combine(settingsDir, DisabledExtensionsFilenameName);
				return Path.Combine(AppDirectories.DataDirectory, DisabledExtensionsFilenameName);
			}
		}

		/// <summary>
		/// Returns all extension files in <paramref name="baseDir"/>
		/// </summary>
		public static IEnumerable<string> GetExtensionFiles(string baseDir) {
			foreach (var f in GetFiles(baseDir, ExtensionSearchPattern))
				yield return f;
			var extensionsSubDir = Path.Combine(baseDir, ExtensionsSubDir);
			if (Directory.Exists(extensionsSubDir)) {
				foreach (var f in GetFiles(extensionsSubDir, ExtensionSearchPattern))
					yield return f;
				foreach (var d in GetDirectories(extensionsSubDir)) {
					foreach (var f in GetFiles(d, ExtensionSearchPattern))
						yield return f;
				}
			}
		}

		/// <summary>
		/// Returns the file names of the disabled extensions
		/// </summary>
		public static HashSet<string> LoadDisabled() {
			var hash = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			try {
				if (File.Exists(DisabledExtensionsFilename)) {
					foreach (var line in File.ReadAllLines(DisabledExtensionsFilename)) {
						var name = line.Trim();
						if (name.Length > 0)
							hash.Add(name);
					}
				}
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
			return hash;
		}

		/// <summary>
		/// Saves the file names of the disabled extensions
		/// </summary>
		public static void SaveDisabled(IEnumerable<string> filenames) {
			try {
				var filename = DisabledExtensionsFilename;
				var dir = Path.GetDirectoryName(filename);
				if (!string.IsNullOrEmpty(dir))
					Directory.CreateDirectory(dir);
				var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
				foreach (var f in filenames) {
					var name = Path.GetFileName(f);
					if (name.Length > 0)
						names.Add(name);
				}
				File.WriteAllLines(filename, names);
			}
			catch (IOException) {
			}
			catch (UnauthorizedAccessException) {
			}
		}

		/// <summary>
		/// Returns true if <paramref name="filename"/> is one of the extensions that ship with dnSpy
		/// </summary>
		public static bool IsBuiltInExtension(string filename) {
			try {
				var dir = Path.GetDirectoryName(filename);
				return dir is not null &&
					string.Equals(Path.GetFullPath(dir), Path.GetFullPath(AppDirectories.BinDirectory), StringComparison.OrdinalIgnoreCase);
			}
			catch {
				return false;
			}
		}

		static string[] GetFiles(string dir, string searchPattern) {
			try {
				return Directory.GetFiles(dir, searchPattern);
			}
			catch {
				return Array.Empty<string>();
			}
		}

		static string[] GetDirectories(string dir) {
			try {
				return Directory.GetDirectories(dir);
			}
			catch {
				return Array.Empty<string>();
			}
		}
	}
}
