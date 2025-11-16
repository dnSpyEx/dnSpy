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
