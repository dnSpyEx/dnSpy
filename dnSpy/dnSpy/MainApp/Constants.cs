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

using System.IO;
using System.Reflection;
using System.Security.Principal;

namespace dnSpy.MainApp {
	static class Constants {
		public const string DnSpy = "dnSpy";
		// Used in filenames so must only have valid filename chars
		public const string DnSpyFile = DnSpy;

		public static bool IsRunningAsAdministrator { get; }

		public static string ExecutablePath { get; }

		static Constants() {
			using var id = WindowsIdentity.GetCurrent();
			IsRunningAsAdministrator = new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);

			ExecutablePath = Assembly.GetEntryAssembly()!.Location;
#if NET
			// Use the native exe and not the managed file
			ExecutablePath = Path.ChangeExtension(ExecutablePath, "exe");
			if (!File.Exists(ExecutablePath)) {
				// All .NET files could be in a bin sub dir
				if (Path.GetDirectoryName(Path.GetDirectoryName(ExecutablePath)) is string baseDir)
					ExecutablePath = Path.Combine(baseDir, Path.GetFileName(ExecutablePath));
			}
#endif
		}
	}
}
