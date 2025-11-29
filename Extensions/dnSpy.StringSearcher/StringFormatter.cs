/*
    Copyright (C) 2025 Washi

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
using System.Text;

namespace dnSpy.StringSearcher {
	// Adapted from CSharpFormatter
	internal static class StringFormatter {
		[ThreadStatic]
		private static StringBuilder? builder;

		public static string ToFormattedString(string value, out bool isVerbatim) {
			if (CanUseVerbatimString(value)) {
				isVerbatim = true;
				return GetFormattedVerbatimString(value);
			}
			else {
				isVerbatim = false;
				return GetFormattedString(value);
			}
		}

		private static bool CanUseVerbatimString(string s) {
			bool foundBackslash = false;
			foreach (var c in s) {
				switch (c) {
				case '"':
					break;

				case '\\':
					foundBackslash = true;
					break;

				case '\a':
				case '\b':
				case '\f':
				case '\n':
				case '\r':
				case '\t':
				case '\v':
				case '\0':
				// More newline chars
				case '\u0085':
				case '\u2028':
				case '\u2029':
					return false;

				default:
					if (char.IsControl(c))
						return false;
					break;
				}
			}
			return foundBackslash;
		}

		private static string GetFormattedString(string value) {
			var sb = GetBuilder(value.Length + 2);

			sb.Append('"');
			foreach (var c in value) {
				switch (c) {
				case '\a': sb.Append(@"\a"); break;
				case '\b': sb.Append(@"\b"); break;
				case '\f': sb.Append(@"\f"); break;
				case '\n': sb.Append(@"\n"); break;
				case '\r': sb.Append(@"\r"); break;
				case '\t': sb.Append(@"\t"); break;
				case '\v': sb.Append(@"\v"); break;
				case '\\': sb.Append(@"\\"); break;
				case '\0': sb.Append(@"\0"); break;
				case '"': sb.Append("\\\""); break;
				default:
					if (char.IsControl(c)) {
						sb.Append(@"\u");
						sb.Append(((ushort)c).ToString("X4"));
					}
					else
						sb.Append(c);
					break;
				}
			}
			sb.Append('"');

			return sb.ToString();
		}

		private static string GetFormattedVerbatimString(string value) {
			var sb = GetBuilder(value.Length + 3);

			sb.Append("@\"");
			foreach (var c in value) {
				if (c == '"')
					sb.Append("\"\"");
				else
					sb.Append(c);
			}
			sb.Append('"');

			return sb.ToString();
		}

		private static StringBuilder GetBuilder(int capacity) {
			if (builder is null) {
				builder = new StringBuilder();
			} else {
				builder.Clear();
			}

			builder.EnsureCapacity(capacity);
			return builder;
		}
	}
}
