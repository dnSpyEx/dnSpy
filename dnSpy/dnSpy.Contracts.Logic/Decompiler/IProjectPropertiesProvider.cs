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

using System.Collections.Generic;
using dnlib.DotNet;

namespace dnSpy.Contracts.Decompiler {
	/// <summary>
	/// Implemented by a decompiler (<see cref="IDecompiler"/>) whose code needs properties in the project files that
	/// "Export to Project" creates, e.g. the C# version of the code it creates (LangVersion)
	/// </summary>
	public interface IProjectPropertiesProvider {
		/// <summary>
		/// Gets the properties (MSBuild property name, value) of the project of a module
		/// </summary>
		/// <param name="module">Module</param>
		/// <returns></returns>
		IEnumerable<KeyValuePair<string, string>> GetProjectProperties(ModuleDef module);
	}
}
