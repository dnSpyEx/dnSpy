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
using System.Diagnostics;
using System.Text;

namespace dnSpy.Documents.Tabs.DocViewer.ToolTips {
	static class NumberUtils {
		internal static string ToString(StringBuilder sb, int @base, long value, int @sizeof) {
			if (@base == 10 && value < 0)
				return "-" + ToString(sb, @base, (ulong)(-value), @sizeof);
			return ToString(sb, @base, unchecked((ulong)value), @sizeof);
		}

		internal static string ToString(StringBuilder sb, int @base, ulong value, int @sizeof) {
			const int digits = 0;
			const bool upper = true;

			switch (@base) {
			case 2:  return ToBinary(sb, value, digits, @sizeof << 3);
			case 8:  return ToOctal(sb, value, digits, @sizeof << 2);
			case 10: return value.ToString();
			case 16: return ToHexadecimal(sb, value, digits, @sizeof << 1, upper);
			default: throw new ArgumentOutOfRangeException(nameof(@base));
			}
		}

		internal static string AddDigitSeparators(StringBuilder sb, string rawNumber, int digitGroupSize, string digitSeparator) {
			Debug.Assert(digitGroupSize > 0);
			Debug.Assert(!string.IsNullOrEmpty(digitSeparator));

			if (rawNumber.Length <= digitGroupSize)
				return rawNumber;

			sb.Clear();
			for (int i = 0; i < rawNumber.Length; i++) {
				int d = rawNumber.Length - i;
				if (i != 0 && (d % digitGroupSize) == 0 && rawNumber[i - 1] != '-')
					sb.Append(digitSeparator);
				sb.Append(rawNumber[i]);
			}

			return sb.ToString();
		}

		internal static string ToFixedSizeHexadecimalArray(StringBuilder sb, ulong value, int bytesCount, bool upper) {
			sb.Clear();

			char hexHigh = upper ? (char)('A' - 10) : (char)('a' - 10);
			while (bytesCount > 0) {
				int ldigit = (int)(value & 0xF);
				value >>= 4;
				int hdigit = (int)(value & 0xF);
				value >>= 4;
				bytesCount--;

				if (hdigit > 9)
					sb.Append((char)(hdigit + hexHigh));
				else
					sb.Append((char)(hdigit + '0'));

				if (ldigit > 9)
					sb.Append((char)(ldigit + hexHigh));
				else
					sb.Append((char)(ldigit + '0'));

				if (bytesCount != 0)
					sb.Append(' ');
			}

			return sb.ToString();
		}

		static string ToHexadecimal(StringBuilder sb, ulong value, int digits, int maxDigits, bool upper) {
			sb.Clear();

			if (digits == 0) {
				digits = 1;
				for (ulong tmp = value; ;) {
					tmp >>= 4;
					if (tmp == 0)
						break;
					digits++;
				}
			}

			if (digits > maxDigits)
				digits = maxDigits;

			char hexHigh = upper ? (char)('A' - 10) : (char)('a' - 10);
			for (int i = 0; i < digits; i++) {
				int digit = (int)((value >> ((digits - i - 1) << 2)) & 0xF);
				if (digit > 9)
					sb.Append((char)(digit + hexHigh));
				else
					sb.Append((char)(digit + '0'));
			}

			return sb.ToString();
		}

		static string ToOctal(StringBuilder sb, ulong value, int digits, int maxDigits) {
			sb.Clear();

			if (digits == 0) {
				digits = 1;
				for (ulong tmp = value; ;) {
					tmp >>= 3;
					if (tmp == 0)
						break;
					digits++;
				}
			}

			if (digits > maxDigits)
				digits = maxDigits;

			for (int i = 0; i < digits; i++) {
				int digit = (int)((value >> (digits - i - 1) * 3) & 7);
				sb.Append((char)(digit + '0'));
			}

			return sb.ToString();
		}

		static string ToBinary(StringBuilder sb, ulong value, int digits, int maxDigits) {
			sb.Clear();

			if (digits == 0) {
				digits = 1;
				for (ulong tmp = value; ;) {
					tmp >>= 1;
					if (tmp == 0)
						break;
					digits++;
				}
			}

			if (digits > maxDigits)
				digits = maxDigits;

			for (int i = 0; i < digits; i++) {
				int digit = (int)((value >> (digits - i - 1)) & 1);
				sb.Append((char)(digit + '0'));
			}

			return sb.ToString();
		}
	}
}
