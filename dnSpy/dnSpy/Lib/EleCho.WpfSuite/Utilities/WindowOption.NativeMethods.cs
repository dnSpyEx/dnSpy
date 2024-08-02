using System;
using System.Runtime.InteropServices;

namespace EleCho.WpfSuite {
	public partial class WindowOption {
		internal static class NativeDefinition {
			public const nint WM_NCHITTEST = 0x0084;
			public const nint WM_NCMOUSELEAVE = 0x02A2;
			public const nint WM_NCLBUTTONDOWN = 0x00A1;
			public const nint WM_NCLBUTTONUP = 0x00A2;
			public const nint WM_MOUSEMOVE = 0x0200;

			public const nint HTCLOSE = 20;
			public const nint HTMAXBUTTON = 9;
			public const nint HTMINBUTTON = 8;
		}
	}
}
