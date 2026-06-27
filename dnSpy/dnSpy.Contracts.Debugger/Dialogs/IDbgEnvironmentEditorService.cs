/*
    Copyright (C) 2024 ElektroKill

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

namespace dnSpy.Contracts.Debugger.Dialogs {
	/// <summary>
	/// Service used to display an edit dialog for a <see cref="DbgEnvironment"/>
	/// </summary>
	public interface IDbgEnvironmentEditorService {
		/// <summary>
		/// Display an edit dialog for the given set of environment variables.
		/// </summary>
		/// <param name="environment">The environment to edit</param>
		/// <returns>true if the environment was modified, false otherwise</returns>
		public bool ShowEditDialog(DbgEnvironment environment);
	}
}
