using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Xml.Linq;
using static EleCho.WpfSuite.WindowOption.NativeDefinition;

namespace EleCho.WpfSuite {
	/// <summary>
	/// Window options
	/// </summary>
	public partial class WindowOption {
		static readonly Version s_versionWindows10_1809 = new Version(10, 0, 17763);
		static readonly Version s_versionWindows10 = new Version(10, 0);

		/// <summary>
		/// DWM Supports Corner, BorderColor, CaptionColor, TextColor
		/// </summary>
		static readonly Version s_versionWindows11_22000 = new Version(10, 0, 22000);

		/// <summary>
		/// DWM Supports Dark mode and Backdrop property
		/// </summary>
		static readonly Version s_versionWindows11_22621 = new Version(10, 0, 22621);

		static readonly Version s_versionCurrentWindows = Environment.OSVersion.Version;

		static Dictionary<nint, Visual>? s_maximumButtons;
		static Dictionary<nint, Visual>? s_minimumButtons;
		static Dictionary<nint, Visual>? s_closeButtons;

		static DependencyPropertyKey s_uiElementIsMouseOverPropertyKey =
			(DependencyPropertyKey)typeof(UIElement).GetField("IsMouseOverPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

		static DependencyPropertyKey s_buttonIsPressedPropertyKey =
			(DependencyPropertyKey)typeof(ButtonBase).GetField("IsPressedPropertyKey", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

		/// <summary>
		/// Get value of IsMaximumButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <returns></returns>
		public static bool GetIsMaximumButton(DependencyObject obj) {
			return (bool)obj.GetValue(IsMaximumButtonProperty);
		}

		/// <summary>
		/// Set value of IsMaximumButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <param name="value"></param>
		public static void SetIsMaximumButton(DependencyObject obj, bool value) {
			obj.SetValue(IsMaximumButtonProperty, value);
		}

		/// <summary>
		/// Get value of IsMinimumButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <returns></returns>
		public static bool GetIsMinimumButton(DependencyObject obj) {
			return (bool)obj.GetValue(IsMinimumButtonProperty);
		}

		/// <summary>
		/// Set value of IsMinimumButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <param name="value"></param>
		public static void SetIsMinimumButton(DependencyObject obj, bool value) {
			obj.SetValue(IsMinimumButtonProperty, value);
		}

		/// <summary>
		/// Get value of IsCloseButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <returns></returns>
		public static bool GetIsCloseButton(DependencyObject obj) {
			return (bool)obj.GetValue(IsCloseButtonProperty);
		}

		/// <summary>
		/// Set value of IsCloseButton property
		/// </summary>
		/// <param name="obj"></param>
		/// <param name="value"></param>
		public static void SetIsCloseButton(DependencyObject obj, bool value) {
			obj.SetValue(IsCloseButtonProperty, value);
		}

		/// <summary>
		/// The DependencyProperty of IsMaximumButton property
		/// </summary>
		public static readonly DependencyProperty IsMaximumButtonProperty =
			DependencyProperty.RegisterAttached("IsMaximumButton", typeof(bool), typeof(WindowOption), new FrameworkPropertyMetadata(false, OnIsMaximumButtonChanged));

		/// <summary>
		/// The DependencyProperty of IsMinimumButton property
		/// </summary>
		public static readonly DependencyProperty IsMinimumButtonProperty =
			DependencyProperty.RegisterAttached("IsMinimumButton", typeof(bool), typeof(WindowOption), new FrameworkPropertyMetadata(false, OnIsMinimumButtonChanged));

		/// <summary>
		/// The DependencyProperty of IsCloseButton property
		/// </summary>
		public static readonly DependencyProperty IsCloseButtonProperty =
			DependencyProperty.RegisterAttached("IsCloseButton", typeof(bool), typeof(WindowOption), new FrameworkPropertyMetadata(false, OnIsCloseButtonChanged));

		#region DependencyProperty Callbacks

		private static void OnIsMaximumButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
			if (d is not FrameworkElement frameworkElement) {
				throw new InvalidOperationException("Target DependencyObject is not FrameworkElement");
			}

			if (DesignerProperties.GetIsInDesignMode(d)) {
				return;
			}

			if (Window.GetWindow(d) is Window window) {
				DoAfterWindowSourceInitialized(window, () => {
					ApplyIsMaximumButton(window, frameworkElement, (bool)e.NewValue);
				});
			}
			else {
				DoAfterElementLoaded(frameworkElement, () => {
					if (Window.GetWindow(frameworkElement) is Window loadedWindow) {
						DoAfterWindowSourceInitialized(loadedWindow, () => {
							ApplyIsMaximumButton(loadedWindow, frameworkElement, (bool)e.NewValue);
						});
					}
					else {
						throw new InvalidOperationException("Cannot find Window of Visual");
					}
				});
			}
		}

		private static void OnIsMinimumButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
			if (d is not FrameworkElement frameworkElement) {
				throw new InvalidOperationException("Target DependencyObject is not FrameworkElement");
			}

			if (DesignerProperties.GetIsInDesignMode(d)) {
				return;
			}

			if (Window.GetWindow(d) is Window window) {
				DoAfterWindowSourceInitialized(window, () => {
					ApplyIsMinimumButton(window, frameworkElement, (bool)e.NewValue);
				});
			}
			else {
				DoAfterElementLoaded(frameworkElement, () => {
					if (Window.GetWindow(frameworkElement) is Window loadedWindow) {
						DoAfterWindowSourceInitialized(loadedWindow, () => {
							ApplyIsMinimumButton(loadedWindow, frameworkElement, (bool)e.NewValue);
						});
					}
					else {
						throw new InvalidOperationException("Cannot find Window of Visual");
					}
				});
			}
		}

		private static void OnIsCloseButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) {
			if (d is not FrameworkElement frameworkElement) {
				throw new InvalidOperationException("Target DependencyObject is not FrameworkElement");
			}

			if (DesignerProperties.GetIsInDesignMode(d)) {
				return;
			}

			if (Window.GetWindow(d) is Window window) {
				DoAfterWindowSourceInitialized(window, () => {
					ApplyIsCloseButton(window, frameworkElement, (bool)e.NewValue);
				});
			}
			else {
				DoAfterElementLoaded(frameworkElement, () => {
					if (Window.GetWindow(frameworkElement) is Window loadedWindow) {
						DoAfterWindowSourceInitialized(loadedWindow, () => {
							ApplyIsCloseButton(loadedWindow, frameworkElement, (bool)e.NewValue);
						});
					}
					else {
						throw new InvalidOperationException("Cannot find Window of Visual");
					}
				});
			}
		}

		private static IntPtr WindowCaptionButtonsInteropHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled) {
			if (handled) {
				return IntPtr.Zero;
			}

			switch ((nint)msg) {
			case NativeDefinition.WM_NCHITTEST: {
				var x = (int)((ulong)lParam & 0x0000FFFF);
				var y = (int)((ulong)lParam & 0xFFFF0000) >> 16;
				var result = default(IntPtr);

				if (s_maximumButtons is not null &&
					s_maximumButtons.TryGetValue(hwnd, out var maximumButtonVisual)) {
					var relativePoint = maximumButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(maximumButtonVisual, relativePoint);

					if (hitResult is not null) {
						maximumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, true);

						handled = true;
						result = NativeDefinition.HTMAXBUTTON;
					}
					else {
						maximumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

						if (maximumButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, false);
						}
					}
				}

				if (s_minimumButtons is not null &&
					s_minimumButtons.TryGetValue(hwnd, out var minimumButtonVisual)) {
					var relativePoint = minimumButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(minimumButtonVisual, relativePoint);

					if (hitResult is not null) {
						minimumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, true);

						handled = true;
						result = NativeDefinition.HTMINBUTTON;
					}
					else {
						minimumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

						if (minimumButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, false);
						}
					}
				}

				if (s_closeButtons is not null &&
					s_closeButtons.TryGetValue(hwnd, out var closeButtonVisual)) {
					var relativePoint = closeButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(closeButtonVisual, relativePoint);

					if (hitResult is not null) {
						closeButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, true);

						handled = true;
						result = NativeDefinition.HTCLOSE;
					}
					else {
						closeButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

						if (closeButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, false);
						}
					}
				}

				return result;
			}

			case NativeDefinition.WM_NCLBUTTONDOWN: {
				var x = (int)((ulong)lParam & 0x0000FFFF);
				var y = (int)((ulong)lParam & 0xFFFF0000) >> 16;

				if (s_maximumButtons is not null &&
					s_maximumButtons.TryGetValue(hwnd, out var maximumButtonVisual)) {
					var relativePoint = maximumButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(maximumButtonVisual, relativePoint);

					if (hitResult is not null) {
						if (maximumButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, true);
						}

						handled = true;
					}
				}

				if (s_minimumButtons is not null &&
					s_minimumButtons.TryGetValue(hwnd, out var minimumButtonVisual)) {
					var relativePoint = minimumButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(minimumButtonVisual, relativePoint);

					if (hitResult is not null) {
						if (minimumButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, true);
						}

						handled = true;
					}
				}

				if (s_closeButtons is not null &&
					s_closeButtons.TryGetValue(hwnd, out var closeButtonVisual)) {
					var relativePoint = closeButtonVisual.PointFromScreen(new Point(x, y));
					var hitResult = VisualTreeHelper.HitTest(closeButtonVisual, relativePoint);

					if (hitResult is not null) {
						if (closeButtonVisual is ButtonBase button) {
							button.SetValue(s_buttonIsPressedPropertyKey, true);
						}

						handled = true;
					}
				}

				break;
			}

			case NativeDefinition.WM_NCLBUTTONUP: {
				var x = (int)((ulong)lParam & 0x0000FFFF);
				var y = (int)((ulong)lParam & 0xFFFF0000) >> 16;

				if (s_maximumButtons is not null &&
					s_maximumButtons.TryGetValue(hwnd, out var maximumButtonVisual)) {
					if (maximumButtonVisual is ButtonBase button) {
						bool shouldClick = false;
						if ((bool)button.GetValue(s_buttonIsPressedPropertyKey.DependencyProperty)) {
							shouldClick = true;
						}

						button.SetValue(s_buttonIsPressedPropertyKey, false);

						if (shouldClick) {
							button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
							button.Command?.Execute(button.CommandParameter);
						}

						handled = true;
					}
				}

				if (s_minimumButtons is not null &&
					s_minimumButtons.TryGetValue(hwnd, out var minimumButtonVisual)) {
					if (minimumButtonVisual is ButtonBase button) {
						bool shouldClick = false;
						if ((bool)button.GetValue(s_buttonIsPressedPropertyKey.DependencyProperty)) {
							shouldClick = true;
						}

						button.SetValue(s_buttonIsPressedPropertyKey, false);

						if (shouldClick) {
							button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
							button.Command?.Execute(button.CommandParameter);
						}

						handled = true;
					}
				}

				if (s_closeButtons is not null &&
					s_closeButtons.TryGetValue(hwnd, out var closeButtonVisual)) {
					if (closeButtonVisual is ButtonBase button) {
						bool shouldClick = false;
						if ((bool)button.GetValue(s_buttonIsPressedPropertyKey.DependencyProperty)) {
							shouldClick = true;
						}

						button.SetValue(s_buttonIsPressedPropertyKey, false);

						if (shouldClick) {
							button.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent, button));
							button.Command?.Execute(button.CommandParameter);
						}

						handled = true;
					}
				}

				break;
			}

			case NativeDefinition.WM_NCMOUSELEAVE: {
				var x = (int)((ulong)lParam & 0x0000FFFF);
				var y = (int)((ulong)lParam & 0xFFFF0000) >> 16;

				if (s_maximumButtons is not null &&
					s_maximumButtons.TryGetValue(hwnd, out var maximumButtonVisual)) {
					maximumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

					if (maximumButtonVisual is ButtonBase button) {
						button.SetValue(s_buttonIsPressedPropertyKey, false);
					}
				}

				if (s_minimumButtons is not null &&
					s_minimumButtons.TryGetValue(hwnd, out var minimumButtonVisual)) {
					minimumButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

					if (minimumButtonVisual is ButtonBase button) {
						button.SetValue(s_buttonIsPressedPropertyKey, false);
					}
				}

				if (s_closeButtons is not null &&
					s_closeButtons.TryGetValue(hwnd, out var closeButtonVisual)) {
					closeButtonVisual.SetValue(s_uiElementIsMouseOverPropertyKey, false);

					if (closeButtonVisual is ButtonBase button) {
						button.SetValue(s_buttonIsPressedPropertyKey, false);
					}
				}

				break;
			}
			}

			return IntPtr.Zero;
		}

		#endregion

		#region Utilities

		private static void DoAfterWindowSourceInitialized(Window window, Action action) {
			var eventHandler = default(EventHandler);

			eventHandler = (s, e) => {
				action?.Invoke();
				window.SourceInitialized -= eventHandler;
			};

			window.SourceInitialized += eventHandler;
		}

		private static void DoAfterElementLoaded(FrameworkElement element, Action action) {
			var eventHandler = default(RoutedEventHandler);

			eventHandler = (s, e) => {
				action?.Invoke();
				element.Loaded -= eventHandler;
			};

			element.Loaded += eventHandler;
		}

		private static bool HasWindowCaptionButton(nint hwnd) {
			if (s_minimumButtons is not null && s_minimumButtons.ContainsKey(hwnd))
				return true;
			if (s_maximumButtons is not null && s_maximumButtons.ContainsKey(hwnd))
				return true;
			if (s_closeButtons is not null && s_closeButtons.ContainsKey(hwnd))
				return true;

			return false;
		}

		#endregion

		#region Final Logic

		private static unsafe void ApplyIsMaximumButton(Window window, Visual visual, bool isMaximumButton) {
			var windowInteropHelper = new WindowInteropHelper(window);
			var windowHandle = windowInteropHelper.EnsureHandle();

			var hwndSource = HwndSource.FromHwnd(windowHandle);

			if (isMaximumButton) {
				if (s_maximumButtons is null) {
					s_maximumButtons = new();
				}

				if (HasWindowCaptionButton(windowHandle)) {
					hwndSource.AddHook(WindowCaptionButtonsInteropHook);
				}

				if (s_maximumButtons.ContainsKey(windowHandle)) {
					throw new InvalidOperationException("MaximumButton is already set to another Visual");
				}

				s_maximumButtons[windowHandle] = visual;
			}
			else {
				if (s_maximumButtons is null) {
					return;
				}

				s_maximumButtons.Remove(windowHandle);

				if (s_maximumButtons.Count == 0) {
					s_maximumButtons = null;
				}

				if (!HasWindowCaptionButton(windowHandle)) {
					hwndSource.RemoveHook(WindowCaptionButtonsInteropHook);
				}
			}
		}

		private static unsafe void ApplyIsMinimumButton(Window window, Visual visual, bool isMinimumButton) {
			var windowInteropHelper = new WindowInteropHelper(window);
			var windowHandle = windowInteropHelper.EnsureHandle();

			var hwndSource = HwndSource.FromHwnd(windowHandle);

			if (isMinimumButton) {
				if (s_minimumButtons is null) {
					s_minimumButtons = new();
				}

				if (HasWindowCaptionButton(windowHandle)) {
					hwndSource.AddHook(WindowCaptionButtonsInteropHook);
				}

				if (s_minimumButtons.ContainsKey(windowHandle)) {
					throw new InvalidOperationException("MinimumButton is already set to another Visual");
				}

				s_minimumButtons[windowHandle] = visual;
			}
			else {
				if (s_minimumButtons is null) {
					return;
				}

				s_minimumButtons.Remove(windowHandle);

				if (s_minimumButtons.Count == 0) {
					s_minimumButtons = null;
				}

				if (!HasWindowCaptionButton(windowHandle)) {
					hwndSource.RemoveHook(WindowCaptionButtonsInteropHook);
				}
			}
		}

		private static unsafe void ApplyIsCloseButton(Window window, Visual visual, bool isCloseButton) {
			var windowInteropHelper = new WindowInteropHelper(window);
			var windowHandle = windowInteropHelper.EnsureHandle();

			var hwndSource = HwndSource.FromHwnd(windowHandle);

			if (isCloseButton) {
				if (s_closeButtons is null) {
					s_closeButtons = new();
				}

				if (HasWindowCaptionButton(windowHandle)) {
					hwndSource.AddHook(WindowCaptionButtonsInteropHook);
				}

				if (s_closeButtons.ContainsKey(windowHandle)) {
					throw new InvalidOperationException("MinimumButton is already set to another Visual");
				}

				s_closeButtons[windowHandle] = visual;
			}
			else {
				if (s_closeButtons is null) {
					return;
				}

				s_closeButtons.Remove(windowHandle);

				if (s_closeButtons.Count == 0) {
					s_closeButtons = null;
				}

				if (!HasWindowCaptionButton(windowHandle)) {
					hwndSource.RemoveHook(WindowCaptionButtonsInteropHook);
				}
			}
		}

		#endregion
	}
}
