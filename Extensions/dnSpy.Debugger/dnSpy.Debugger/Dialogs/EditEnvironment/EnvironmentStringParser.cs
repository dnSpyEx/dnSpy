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

using System.Collections.Generic;

namespace dnSpy.Debugger.Dialogs.EditEnvironment {
	public struct EnvironmentStringParser {
		readonly string str;
		int pos;

		public EnvironmentStringParser(string str) => this.str = str;

		public bool TryParse(out Dictionary<string, string> env) {
			env = new Dictionary<string, string>();

			for (;;) {
				var key = GetNextToken();
				if (key.Kind == TokenKind.EOF)
					break;
				if (key.Kind != TokenKind.String)
					return false;
				var assign = GetNextToken();
				if (assign.Kind != TokenKind.Assign)
					return false;
				var value = GetNextToken();
				if (value.Kind != TokenKind.String)
					return false;

				env[key.Value] = value.Value;

				var seperator = GetNextToken();
				if (seperator.Kind == TokenKind.EOF)
					break;
				if (seperator.Kind != TokenKind.Separator)
					return false;
			}

			return true;
		}

		Token GetNextToken() {
			if (pos >= str.Length)
				return new Token(TokenKind.EOF);

			var currentChar = str[pos++];
			if (currentChar == ';')
				return new Token(TokenKind.Separator);
			if (currentChar == '=')
				return new Token(TokenKind.Assign);

			if (currentChar == '"') {
				int stringStartPos = pos;
				while (true) {
					if (pos >= str.Length)
						return new Token(TokenKind.Invalid);
					if (str[pos++] == '"')
						break;
				}
				if (pos == stringStartPos)
					return new Token(TokenKind.Invalid);
				return new Token(TokenKind.String, str.Substring(stringStartPos, pos - stringStartPos - 1));
			}

			int startPos = pos - 1;
			while (true) {
				if (pos >= str.Length || str[pos] is ';' or '=')
					break;
				pos++;
			}
			return new Token(TokenKind.String, str.Substring(startPos, pos - startPos));
		}

		enum TokenKind {
			Invalid,
			Separator,
			Assign,
			String,
			EOF,
		}

		readonly struct Token {
			public readonly TokenKind Kind;
			public readonly string Value;

			public Token(TokenKind kind) {
				Kind = kind;
				Value = string.Empty;
			}

			public Token(TokenKind kind, string value) {
				Kind = kind;
				Value = value;
			}

			public override string ToString() => Kind == TokenKind.String ? Value : Kind.ToString();
		}
	}
}
