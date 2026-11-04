// Copyright (c) 2010-2013 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using System.Globalization;
using System.Text;

namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	/// <summary>
	/// Escapes strings for the IL output (from NRefactory's TextWriterTokenWriter, which the IL disassembler used)
	/// </summary>
	static class ILStringEscaper {
		/// <summary>
		/// Gets the escape sequence for the specified character.
		/// </summary>
		/// <remarks>This method does not convert ' or ".</remarks>
		static string ConvertChar(char ch) {
			switch (ch) {
			case '\\':
				return "\\\\";
			case '\0':
				return "\\0";
			case '\a':
				return "\\a";
			case '\b':
				return "\\b";
			case '\f':
				return "\\f";
			case '\n':
				return "\\n";
			case '\r':
				return "\\r";
			case '\t':
				return "\\t";
			case '\v':
				return "\\v";
			case ' ':
			case '_':
			case '`':
			case '^':
				// ASCII characters we allow directly in the output even though we don't use
				// other Unicode characters of the same category.
				return ch.ToString();
			case '�':
				return "\\u" + ((int)ch).ToString("x4");
			default:
				if (MustEscape(ch))
					return "\\u" + ((int)ch).ToString("x4");
				return ch.ToString();
			}
		}

		static bool MustEscape(char ch) {
			switch (char.GetUnicodeCategory(ch)) {
			case UnicodeCategory.NonSpacingMark:
			case UnicodeCategory.SpacingCombiningMark:
			case UnicodeCategory.EnclosingMark:
			case UnicodeCategory.LineSeparator:
			case UnicodeCategory.ParagraphSeparator:
			case UnicodeCategory.Control:
			case UnicodeCategory.Format:
			case UnicodeCategory.Surrogate:
			case UnicodeCategory.PrivateUse:
			case UnicodeCategory.ConnectorPunctuation:
			case UnicodeCategory.ModifierSymbol:
			case UnicodeCategory.OtherNotAssigned:
			case UnicodeCategory.SpaceSeparator:
				return true;
			default:
				return false;
			}
		}

		/// <summary>
		/// Converts special characters to escape sequences within the given string.
		/// </summary>
		public static string ConvertString(string str) => ConvertString(str, -1);

		public static string ConvertStringMaxLength(string str, int maxChars) => ConvertString(str, maxChars);

		static string ConvertString(string str, int maxChars) {
			int length = str.Length;
			bool truncated = false;
			if (maxChars > 0 && length > maxChars) {
				length = maxChars;
				truncated = true;
			}
			const string TRUNC_MSG = "[...string is too long...]";
			int i = 0;
			for (; ; i++) {
				if (i >= length) {
					if (length != str.Length)
						str = str.Substring(0, length);
					if (truncated)
						return str + TRUNC_MSG;
					return str;
				}
				char c = str[i];
				switch (c) {
				case '"':
				case '\\':
				case '\0':
				case '\a':
				case '\b':
				case '\f':
				case '\n':
				case '\r':
				case '\t':
				case '\v':
					goto escapeChars;
				case ' ':
				case '_':
				case '`':
				case '^':
					break;
				case '�':
					goto escapeChars;
				default:
					if (MustEscape(c))
						goto escapeChars;
					break;
				}
			}

			escapeChars:
			var sb = new StringBuilder();
			if (i > 0)
				sb.Append(str, 0, i);
			for (; i < length; i++) {
				char ch = str[i];
				if (ch == '"')
					sb.Append("\\\"");
				else
					sb.Append(ConvertChar(ch));
			}
			if (truncated)
				sb.Append(TRUNC_MSG);
			return sb.ToString();
		}
	}
}
