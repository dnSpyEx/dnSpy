using System;
using System.Runtime.InteropServices;

namespace EleCho.WpfSuite {
	public partial class WindowOption {
		internal static class NativeDefinition {
			public const int GWL_STYLE = -16;

			public const nint WS_CAPTION = 0x00C00000;
			public const nint WS_SYSMENU = 0x00080000;

			public const nint WM_NCHITTEST = 0x0084;
			public const nint WM_NCMOUSELEAVE = 0x02A2;
			public const nint WM_NCLBUTTONDOWN = 0x00A1;
			public const nint WM_NCLBUTTONUP = 0x00A2;
			public const nint WM_MOUSEMOVE = 0x0200;

			public const nint HTCLOSE = 20;
			public const nint HTMAXBUTTON = 9;
			public const nint HTMINBUTTON = 8;

			[DllImport("DWMAPI")]
			public static extern nint DwmSetWindowAttribute(nint hwnd, DwmWindowAttribute attribute, nint dataPointer, uint dataSize);

			[DllImport("DWMAPI")]
			public static extern nint DwmExtendFrameIntoClientArea(nint hwnd, ref Margins margins);

			[DllImport("User32")]
			public static extern bool SetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

			[DllImport("User32")]
			public static extern bool GetWindowCompositionAttribute(nint hwnd, ref WindowCompositionAttributeData data);

			[DllImport("User32")]
			public static extern bool SetWindowPos(nint hwnd, nint hwndInsertAfter, int x, int y, int width, int height, SetWindowPosFlags flags);

			[DllImport("User32")]
			public static extern bool InvalidateRect(IntPtr hWnd, IntPtr lpRect, bool bErase);

			[DllImport("User32")]
			public static extern bool UpdateWindow(nint hwnd);

			[DllImport("User32")]
			public extern static nint GetWindowLongPtr(nint hwnd, int index);

			[DllImport("User32")]
			public extern static nint SetWindowLongPtr(nint hwnd, int index, nint newLong);


			[StructLayout(LayoutKind.Sequential)]
			public struct WindowCompositionAttributeData {
				/// <summary>
				/// A flag describing which value to get or set, specified as a value of the <see cref="WindowCompositionAttribute"/> enumeration. 
				/// This parameter specifies which attribute to get or set, and the pvData member points to an object containing the attribute value.
				/// </summary>
				public WindowCompositionAttribute Attribute;

				/// <summary>
				/// When used with the GetWindowCompositionAttribute function, this member contains a pointer to a variable that will hold the value of the requested attribute when the function returns. <br/>
				/// When used with the SetWindowCompositionAttribute function, it points an object containing the attribute value to set. <br/>
				/// The type of the value set depends on the value of the Attrib member.
				/// </summary>
				public nint DataPointer;

				/// <summary>
				/// The size of the object pointed to by the pvData member, in bytes.
				/// </summary>
				public uint DataSize;
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct AccentPolicy {
				public AccentState AccentState;
				public AccentFlags AccentFlags;
				public int GradientColor;
				public int AnimationId;
			}

			[StructLayout(LayoutKind.Sequential)]
			public struct Margins {
				public int LeftWidth;
				public int RightWidth;
				public int TopHeight;
				public int BottomHeight;
			}

			public enum AccentState {
				Disabled,
				EnableGradient = 1,
				EnableTransparent = 2,
				EnableBlurBehind = 3,
				EnableAcrylicBlurBehind = 4,
				EnableHostBackdrop = 5,
				InvalidState = 6,
			}

			[Flags]
			public enum AccentFlags {
				None = 0,
				ExtendSize = 0x4,
				LeftBorder = 0x20,
				TopBorder = 0x40,
				RightBorder = 0x80,
				BottomBorder = 0x100,
				AllBorder = LeftBorder | TopBorder | RightBorder | BottomBorder,
			}

			public enum WindowCompositionAttribute {
				// 省略其他未使用的字段
				WcaAccentPolicy = 19,
				// 省略其他未使用的字段
			}

			public enum DwmWindowAttribute {
				NCRENDERING_ENABLED,
				NCRENDERING_POLICY,
				TRANSITIONS_FORCEDISABLED,
				ALLOW_NCPAINT,
				CAPTION_BUTTON_BOUNDS,
				NONCLIENT_RTL_LAYOUT,
				FORCE_ICONIC_REPRESENTATION,
				FLIP3D_POLICY,
				EXTENDED_FRAME_BOUNDS,
				HAS_ICONIC_BITMAP,
				DISALLOW_PEEK,
				EXCLUDED_FROM_PEEK,
				CLOAK,
				CLOAKED,
				FREEZE_REPRESENTATION,
				PASSIVE_UPDATE_MODE,
				USE_HOSTBACKDROPBRUSH,
				USE_IMMERSIVE_DARK_MODE = 20,
				WINDOW_CORNER_PREFERENCE = 33,
				BORDER_COLOR,
				CAPTION_COLOR,
				TEXT_COLOR,
				VISIBLE_FRAME_BORDER_THICKNESS,
				SYSTEMBACKDROP_TYPE,
				LAST
			}

			// Token: 0x0200002E RID: 46
			[Flags]
			public enum SetWindowPosFlags {
				// Token: 0x04000368 RID: 872
				ASYNCWINDOWPOS = 16384,
				// Token: 0x04000369 RID: 873
				DEFERERASE = 8192,
				// Token: 0x0400036A RID: 874
				DRAWFRAME = 32,
				// Token: 0x0400036B RID: 875
				FRAMECHANGED = 32,
				// Token: 0x0400036C RID: 876
				HIDEWINDOW = 128,
				// Token: 0x0400036D RID: 877
				NOACTIVATE = 16,
				// Token: 0x0400036E RID: 878
				NOCOPYBITS = 256,
				// Token: 0x0400036F RID: 879
				NOMOVE = 2,
				// Token: 0x04000370 RID: 880
				NOOWNERZORDER = 512,
				// Token: 0x04000371 RID: 881
				NOREDRAW = 8,
				// Token: 0x04000372 RID: 882
				NOREPOSITION = 512,
				// Token: 0x04000373 RID: 883
				NOSENDCHANGING = 1024,
				// Token: 0x04000374 RID: 884
				NOSIZE = 1,
				// Token: 0x04000375 RID: 885
				NOZORDER = 4,
				// Token: 0x04000376 RID: 886
				SHOWWINDOW = 64
			}
		}
	}
}
