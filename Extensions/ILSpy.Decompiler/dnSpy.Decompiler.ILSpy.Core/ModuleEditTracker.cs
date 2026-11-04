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

using System.Runtime.CompilerServices;
using dnlib.DotNet;

namespace dnSpy.Decompiler.ILSpy.Core {
	/// <summary>
	/// Remembers which modules have been edited since they were loaded. Their original image is out of date so
	/// the upstream decompiler is given an image that dnlib writes from the edited module. dnSpy doesn't reload a
	/// module when it's saved, so it stays edited.
	/// </summary>
	sealed class ModuleEditTracker {
		/// <summary>
		/// Being edited is a property of the module: all the users of the upstream decompiler share this one
		/// (<see cref="UpstreamCSharpLanguage"/> is told about edits, <see cref="UpstreamILAst"/> isn't)
		/// </summary>
		public static ModuleEditTracker Shared { get; } = new ModuleEditTracker();

		readonly ConditionalWeakTable<ModuleDef, object> modified = new ConditionalWeakTable<ModuleDef, object>();
		static readonly object marker = new object();

		public bool IsModified(ModuleDef module) => modified.TryGetValue(module, out _);

		/// <summary>
		/// Returns true if it wasn't already marked as modified
		/// </summary>
		public bool MarkModified(ModuleDef module) {
			lock (modified) {
				if (modified.TryGetValue(module, out _))
					return false;
				modified.Add(module, marker);
				return true;
			}
		}
	}
}
