using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Media;

namespace EleCho.WpfSuite {
	/// <summary>
	/// Color for WindowOption
	/// </summary>
	[TypeConverter(typeof(WindowOptionColorConverter))]
	[StructLayout(LayoutKind.Sequential)]
	public struct WindowOptionColor {
		private int _value;

		/// <summary>
		/// Integer value
		/// </summary>
		public int Value {
			get => _value;
			set => _value = value;
		}

		/// <summary>
		/// Red channel value
		/// </summary>
		public byte R {
			get => (byte)(_value);
			set => _value = (_value & ~0xFF) + value;
		}

		/// <summary>
		/// Green channel value
		/// </summary>
		public byte G {
			get => (byte)(_value >> 8);
			set => _value = (_value & ~0xFF00) + (value << 8);
		}

		/// <summary>
		/// Blue channel value
		/// </summary>
		public byte B {
			get => (byte)(_value >> 16);
			set => _value = (_value & ~0xFF0000) + (value << 16);
		}

		/// <summary>
		/// Extra byte
		/// </summary>
		public byte Extra {
			get => (byte)(_value >> 24);
			set => _value = (int)(_value & ~0xFF000000) + (value << 24);
		}

		/// <summary>
		/// Create an empty window option color
		/// </summary>
		public WindowOptionColor() {

		}

		/// <summary>
		/// Create a window option color with specified channel values
		/// </summary>
		/// <param name="r">Red channel value</param>
		/// <param name="g">Green channel value</param>
		/// <param name="b">Blue channel value</param>
		public WindowOptionColor(byte r, byte g, byte b) : this(r, g, b, 0xFF) { }

		/// <summary>
		/// Create a window option color with specified channel values and extra byte
		/// </summary>
		/// <param name="r">Red channel value</param>
		/// <param name="g">Green channel value</param>
		/// <param name="b">Blue channel value</param>
		/// <param name="extra">Extra byte</param>
		public WindowOptionColor(byte r, byte g, byte b, byte extra) {
			_value = r | g << 8 | b << 16 | extra << 24;
		}

		/// <summary>
		/// Create a window option color with specified value
		/// </summary>
		/// <param name="value"></param>
		public WindowOptionColor(int value) {
			_value = value;
		}

		/// <summary>
		/// Default color
		/// </summary>
		public static WindowOptionColor Default { get; } = new WindowOptionColor(unchecked((int)0xFFFFFFFF));

		/// <summary>
		/// No color
		/// </summary>
		public static WindowOptionColor None { get; } = new WindowOptionColor(unchecked((int)0xFFFFFFFE));


		/// <summary>
		/// Convert window option color to color
		/// </summary>
		/// <param name="color"></param>
		public static implicit operator Color(WindowOptionColor color) => Color.FromArgb(0xFF, color.R, color.G, color.B);

		/// <summary>
		/// Convert color to window option color
		/// </summary>
		/// <param name="color"></param>
		public static implicit operator WindowOptionColor(Color color) => new WindowOptionColor(color.R, color.G, color.B, 0);
	}
}
