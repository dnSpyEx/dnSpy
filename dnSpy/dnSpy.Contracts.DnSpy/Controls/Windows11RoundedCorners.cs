using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace dnSpy.Contracts.Controls {
	/// <summary>
	/// Rounded corner types
	/// </summary>
	public enum RoundedCornerType : uint {
		/// <summary>
		///	The default behavior. The system determines whether to enable rounded corners
		/// </summary>
		Default = 0,
		/// <summary>
		/// No rounded corners
		/// </summary>
		None = 1,
		/// <summary>
		/// Regular rounded corners
		/// </summary>
		Regular = 2,
		/// <summary>
		/// Small rounded corners
		/// </summary>
		Small = 3
	}

	/// <summary>
	/// Enabled Windows 11 rounded corners for a <see cref="Window"/> or other supported control
	/// </summary>
	public sealed class Windows11RoundedCorners {
		static readonly bool isWindows11 = Environment.OSVersion.Version is { Major: 10, Build: >= 22000 };

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
		public static readonly DependencyProperty TypeProperty = DependencyProperty.RegisterAttached(
			"Type", typeof(RoundedCornerType), typeof(Windows11RoundedCorners), new UIPropertyMetadata(RoundedCornerType.Default, TypePropertyChangedCallback));

		public static void SetType(FrameworkElement element, RoundedCornerType value) => element.SetValue(TypeProperty, value);
		public static RoundedCornerType GetType(FrameworkElement element) => (RoundedCornerType)element.GetValue(TypeProperty);
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member

		static void TypePropertyChangedCallback(DependencyObject d, DependencyPropertyChangedEventArgs e) {
			if (!isWindows11 || d is not IInputElement inputElement)
				return;
			PresentationSource.AddSourceChangedHandler(inputElement, (_, args) => {
				if (args.NewSource is not HwndSource hwndSource)
					return;
				IntPtr hwnd = hwndSource.Handle;
				if (hwnd == IntPtr.Zero)
					return;

				const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
				var attrValue = (RoundedCornerType)e.NewValue;
				DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref attrValue, sizeof(uint));
			});
		}

		[DllImport("dwmapi", CharSet = CharSet.Unicode, PreserveSig = false)]
		static extern void DwmSetWindowAttribute(IntPtr hwnd, uint attribute, ref RoundedCornerType pvAttribute, uint cbAttribute);
	}
}
