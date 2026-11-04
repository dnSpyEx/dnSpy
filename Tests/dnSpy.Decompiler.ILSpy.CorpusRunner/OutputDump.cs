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

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// Each type's output in a file, DIR/ASSEMBLY/NAMESPACE.TYPE.cs, so two runs (e.g. before and after a decompiler
	/// update) can be compared with a diff tool. A type that fails has the exception's first line instead.
	/// </summary>
	sealed class OutputDump {
		readonly string dir;
		readonly HashSet<string> usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		static readonly UTF8Encoding encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

		OutputDump(string dir) => this.dir = dir;

		/// <summary>
		/// DIR/ASSEMBLY (the assembly's file name), emptied first: a crashed run's retry writes it again
		/// </summary>
		public static OutputDump? Create(string root, string assemblyFile) {
			if (root.Length == 0)
				return null;
			var dir = Path.Combine(root, Path.GetFileName(assemblyFile));
			if (Directory.Exists(dir))
				Directory.Delete(dir, recursive: true);
			Directory.CreateDirectory(dir);
			return new OutputDump(dir);
		}

		public void Write(TypeDef type, string text) =>
			File.WriteAllText(Path.Combine(dir, GetFileName(type.FullName)), text.Replace("\r\n", "\n"), encoding);

		static readonly HashSet<string> reservedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
			"CON", "PRN", "AUX", "NUL",
			"COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
			"LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
		};

		/// <summary>
		/// A file name that's valid on Windows and Linux and unique in the assembly's folder, also on case-insensitive
		/// file systems (types are written in metadata order, so the names are the same in each run)
		/// </summary>
		string GetFileName(string typeName) {
			var sb = new StringBuilder(typeName.Length);
			foreach (var c in typeName)
				sb.Append(c < 128 && (char.IsLetterOrDigit(c) || c == '.' || c == '_' || c == '-') ? c : '_');
			var name = sb.Length > 120 ? sb.ToString(0, 120) : sb.ToString();
			name = name.TrimEnd('.');
			if (name.Length == 0 || reservedNames.Contains(name.Split('.')[0]))
				name = "_" + name;
			var candidate = name;
			for (int i = 2; !usedNames.Add(candidate); i++)
				candidate = $"{name}~{i}";
			return candidate + ".cs";
		}
	}
}
