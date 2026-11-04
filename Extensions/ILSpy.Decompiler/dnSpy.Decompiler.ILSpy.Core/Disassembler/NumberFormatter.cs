#pragma warning disable CS3001 // Argument type is not CLS-compliant
using System;

// From dnSpy's NRefactory fork (ICSharpCode.NRefactory/NumberFormatter.cs), used by the IL disassembler
namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	public abstract class NumberFormatter {
		public abstract string Format(sbyte value);
		public abstract string Format(byte value);
		public abstract string Format(short value);
		public abstract string Format(ushort value);
		public abstract string Format(int value);
		public abstract string Format(uint value);
		public abstract string Format(long value);
		public abstract string Format(ulong value);

		public static NumberFormatter GetCSharpInstance(bool hex, bool upper) {
			if (hex)
				return upper ? csharpHexUpper : csharpHexLower;
			return decimalFormatter;
		}

		static readonly DecimalNumberFormatter decimalFormatter = new DecimalNumberFormatter();
		static readonly HexNumberFormatter csharpHexUpper = new HexNumberFormatter("0x", upper: true);
		static readonly HexNumberFormatter csharpHexLower = new HexNumberFormatter("0x", upper: false);
	}

	sealed class DecimalNumberFormatter : NumberFormatter {
		public override string Format(sbyte value) => value.ToString();
		public override string Format(byte value) => value.ToString();
		public override string Format(short value) => value.ToString();
		public override string Format(ushort value) => value.ToString();
		public override string Format(int value) => value.ToString();
		public override string Format(uint value) => value.ToString();
		public override string Format(long value) => value.ToString();
		public override string Format(ulong value) => value.ToString();
	}

	sealed class HexNumberFormatter : NumberFormatter {
		readonly string prefix;
		readonly bool upper;

		public HexNumberFormatter(string prefix, bool upper) {
			this.prefix = prefix ?? throw new ArgumentNullException(nameof(prefix));
			this.upper = upper;
		}

		string Create(string s) => prefix + s;

		public override string Format(sbyte value) {
			if (value < 0)
				return "-" + Format(unchecked((byte)(-value)));
			return Format((byte)value);
		}

		public override string Format(byte value) => value <= 9 ? value.ToString() : Create(value.ToString(upper ? "X" : "x"));

		public override string Format(short value) {
			if (value < 0)
				return "-" + Format(unchecked((ushort)(-value)));
			return Format((ushort)value);
		}

		public override string Format(ushort value) => value <= 9 ? value.ToString() : Create(value.ToString(upper ? "X" : "x"));

		public override string Format(int value) {
			if (value < 0)
				return "-" + Format(unchecked((uint)(-value)));
			return Format((uint)value);
		}

		public override string Format(uint value) => value <= 9 ? value.ToString() : Create(value.ToString(upper ? "X" : "x"));

		public override string Format(long value) {
			if (value < 0)
				return "-" + Format(unchecked((ulong)(-value)));
			return Format((ulong)value);
		}

		public override string Format(ulong value) => value <= 9 ? value.ToString() : Create(value.ToString(upper ? "X" : "x"));
	}
}
#pragma warning restore CS3001 // Argument type is not CLS-compliant
