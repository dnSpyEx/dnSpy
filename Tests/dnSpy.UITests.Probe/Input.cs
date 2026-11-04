/*
    Copyright (C) 2026 dnSpy contributors

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
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace dnSpy.UITests.Probe {
	/// <summary>
	/// Keyboard and mouse input inside dnSpy, without the real keyboard or the focus: the key events go through WPF's
	/// routing to the focused element like real keys (dnSpy's shortcuts are PreviewKeyDown handlers and WPF key bindings),
	/// with the modifier keys set in the UI thread's key state (what WPF's KeyboardDevice reads). Buttons, check boxes and
	/// menu items are invoked with their automation peers, like UI Automation does.
	/// </summary>
	static class Input {
		[DllImport("user32.dll")]
		static extern bool GetKeyboardState(byte[] lpKeyState);
		[DllImport("user32.dll")]
		static extern bool SetKeyboardState(byte[] lpKeyState);

		const int VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12;
		const int VK_LSHIFT = 0xA0, VK_LCONTROL = 0xA2, VK_LMENU = 0xA4;

		/// <summary>
		/// The window that would get the keyboard: the active one, else the newest dialog, else the main window
		/// </summary>
		public static Window KeyboardWindow() {
			var windows = Application.Current.Windows.OfType<Window>().Where(a => a.IsVisible).ToList();
			return windows.FirstOrDefault(a => a.IsActive && a != Application.Current.MainWindow && IsDialog(a))
				?? windows.LastOrDefault(a => a != Application.Current.MainWindow && IsDialog(a))
				?? Application.Current.MainWindow;
		}

		// (Floating tool windows aren't dialogs)
		static bool IsDialog(Window window) => window.Owner is not null || window.GetType().Name.EndsWith("Dlg", StringComparison.Ordinal);

		static UIElement KeyboardTarget(Window window) {
			if (Keyboard.FocusedElement is UIElement focused && Window.GetWindow(focused) == window)
				return focused;
			return FocusManager.GetFocusedElement(window) as UIElement ?? window;
		}

		/// <summary>
		/// Presses a key: "F10", "Ctrl+Shift+E", "Alt+Ctrl+C", "Shift+F11", "Escape". Returns the element that got it.
		/// </summary>
		public static string PressKey(string keys) {
			var parts = keys.Split('+').Select(a => a.Trim()).ToArray();
			var modifiers = ModifierKeys.None;
			foreach (var m in parts.Take(parts.Length - 1)) {
				modifiers |= m.ToLowerInvariant() switch {
					"ctrl" or "control" => ModifierKeys.Control,
					"shift" => ModifierKeys.Shift,
					"alt" => ModifierKeys.Alt,
					_ => throw new ProbeException("Unknown modifier " + m),
				};
			}
			var keyName = parts[^1];
			if (!Enum.TryParse<Key>(keyName, true, out var key))
				throw new ProbeException("Unknown key " + keyName);

			var window = KeyboardWindow();
			var target = KeyboardTarget(window);
			var source = PresentationSource.FromVisual(target as Visual ?? window) ?? PresentationSource.FromVisual(window)
				?? throw new ProbeException("No presentation source for the keyboard target");

			// (Later: a key can open a modal dialog, which only returns when it closes)
			target.Dispatcher.BeginInvoke(new Action(() => RaiseKey(target, source, key, modifiers)));
			return $"{target.GetType().Name} in {window.GetType().Name}";
		}

		static void RaiseKey(UIElement target, PresentationSource source, Key key, ModifierKeys modifiers) {
			var saved = new byte[256];
			GetKeyboardState(saved);
			var state = (byte[])saved.Clone();
			void Set(int vk, int lvk, bool down) {
				state[vk] = state[lvk] = down ? (byte)0x80 : (byte)0;
			}
			Set(VK_CONTROL, VK_LCONTROL, (modifiers & ModifierKeys.Control) != 0);
			Set(VK_SHIFT, VK_LSHIFT, (modifiers & ModifierKeys.Shift) != 0);
			Set(VK_MENU, VK_LMENU, (modifiers & ModifierKeys.Alt) != 0);
			SetKeyboardState(state);
			try {
				var down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyDownEvent };
				target.RaiseEvent(down);
				if (!down.Handled) {
					down = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyDownEvent };
					target.RaiseEvent(down);
				}
				var up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.PreviewKeyUpEvent };
				target.RaiseEvent(up);
				if (!up.Handled) {
					up = new KeyEventArgs(Keyboard.PrimaryDevice, source, Environment.TickCount, key) { RoutedEvent = Keyboard.KeyUpEvent };
					target.RaiseEvent(up);
				}
			}
			finally {
				SetKeyboardState(saved);
			}
		}

		// ---- Dialogs

		public static Window FindWindow(string typeName) =>
			Application.Current.Windows.OfType<Window>().LastOrDefault(a => a.IsVisible && a.GetType().FullName!.EndsWith(typeName, StringComparison.Ordinal))
				?? throw new ProbeException("No window " + typeName);

		static IEnumerable<DependencyObject> Descendants(DependencyObject root) {
			var stack = new Stack<DependencyObject>();
			stack.Push(root);
			while (stack.Count > 0) {
				var node = stack.Pop();
				yield return node;
				int count = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetChildrenCount(node) : 0;
				for (int i = count - 1; i >= 0; i--)
					stack.Push(VisualTreeHelper.GetChild(node, i));
			}
		}

		/// <summary>
		/// The name a screen reader (UI Automation) gets, without the access key's underscore
		/// </summary>
		static string NameOf(UIElement element) {
			string? name = null;
			try {
				name = UIElementAutomationPeer.CreatePeerForElement(element)?.GetName();
			}
			catch {
			}
			if (string.IsNullOrEmpty(name) && element is ContentControl { Content: string s })
				name = s;
			if (string.IsNullOrEmpty(name) && element is HeaderedItemsControl { Header: string h })
				name = h;
			return (name ?? string.Empty).Replace("_", string.Empty).Trim();
		}

		static T Find<T>(Window window, string name) where T : FrameworkElement {
			var n = name.Replace("_", string.Empty).Trim();
			var match = Descendants(window).OfType<T>().FirstOrDefault(a => a.Name == name && a.IsVisible)
				?? Descendants(window).OfType<T>().FirstOrDefault(a => a.IsVisible && NameOf(a) == n);
			return match ?? throw new ProbeException($"No visible {typeof(T).Name} '{name}' in {window.GetType().Name}: " +
				string.Join(", ", Descendants(window).OfType<T>().Where(a => a.IsVisible).Select(a => $"'{(a.Name.Length != 0 ? a.Name : NameOf(a))}'")));
		}

		/// <summary>
		/// Invokes it later: a click can open a modal dialog, which only returns when it closes
		/// </summary>
		static void Invoke(UIElement element) {
			var peer = UIElementAutomationPeer.CreatePeerForElement(element) ?? throw new ProbeException("No automation peer");
			var invoke = peer.GetPattern(PatternInterface.Invoke) as IInvokeProvider ?? throw new ProbeException("Can't invoke " + element.GetType().Name);
			element.Dispatcher.BeginInvoke(new Action(invoke.Invoke));
		}

		public static string Click(string windowType, string button) {
			var b = Find<ButtonBase>(FindWindow(windowType), button);
			if (!b.IsEnabled)
				throw new ProbeException($"Button '{button}' is disabled");
			Invoke(b);
			return NameOf(b);
		}

		public static void SetText(string windowType, string name, string text) {
			var textBox = Find<TextBox>(FindWindow(windowType), name);
			textBox.Text = text;
			textBox.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
			// (Real input makes WPF ask the commands again if they can run, e.g. enables OK)
			CommandManager.InvalidateRequerySuggested();
		}

		public static void SetChecked(string windowType, string name, bool value) {
			var checkBox = Find<CheckBox>(FindWindow(windowType), name);
			checkBox.IsChecked = value;
			checkBox.GetBindingExpression(ToggleButton.IsCheckedProperty)?.UpdateSource();
			CommandManager.InvalidateRequerySuggested();
		}

		/// <summary>
		/// What Escape does in a dialog (WPF does it with the access key manager, which only real key presses use): the
		/// Cancel button (IsCancel), or closes the window. Returns false if no dialog is open.
		/// </summary>
		public static bool CancelDialog() {
			var window = Application.Current.Windows.OfType<Window>().LastOrDefault(a => a.IsVisible && a != Application.Current.MainWindow && IsDialog(a));
			if (window is null)
				return false;
			var cancel = Descendants(window).OfType<Button>().FirstOrDefault(a => a.IsCancel && a.IsVisible && a.IsEnabled);
			if (cancel is not null)
				Invoke(cancel);
			else
				window.Dispatcher.BeginInvoke(new Action(window.Close));
			return true;
		}

		public static bool? IsChecked(string windowType, string name) => Find<CheckBox>(FindWindow(windowType), name).IsChecked;

		// ---- Menus

		/// <summary>
		/// Opens a main menu (or a submenu) or invokes its item: the items are created when their menu opens, so each
		/// level is a separate call
		/// </summary>
		public static bool MenuStep(string[] path, int level) {
			var main = Application.Current.MainWindow;
			IEnumerable<MenuItem> items = Descendants(main).OfType<Menu>().First().Items.OfType<object>()
				.Select(a => a as MenuItem ?? (MenuItem)Descendants(main).OfType<Menu>().First().ItemContainerGenerator.ContainerFromItem(a));
			MenuItem? item = null;
			for (int i = 0; i <= level; i++) {
				var name = path[i].Replace("_", string.Empty).Trim();
				item = items.FirstOrDefault(a => a is not null && NameOf(a) == name)
					?? throw new ProbeException($"No menu item '{path[i]}': " + string.Join(", ", items.Where(a => a is not null).Select(NameOf)));
				if (i < level)
					items = item.Items.OfType<object>().Select(a => a as MenuItem ?? (MenuItem)item.ItemContainerGenerator.ContainerFromItem(a));
			}
			if (level == path.Length - 1) {
				// Close the menus first, like a click on the item does
				foreach (var top in Descendants(main).OfType<Menu>().First().Items.OfType<MenuItem>())
					top.IsSubmenuOpen = false;
				Invoke(item!);
				return true;
			}
			item!.IsSubmenuOpen = true;
			item.UpdateLayout();
			return false;
		}

		// ---- Screenshots

		/// <summary>
		/// Renders dnSpy's visible windows to PNG files (works when dnSpy isn't on the screen)
		/// </summary>
		public static List<string> Screenshot(string pathWithoutExtension) {
			var files = new List<string>();
			int n = 0;
			foreach (var window in Application.Current.Windows.OfType<Window>().Where(a => a.IsVisible && a.ActualWidth > 0 && a.ActualHeight > 0)) {
				var content = window.Content as Visual ?? window;
				var element = content as FrameworkElement ?? window;
				int width = (int)Math.Ceiling(element.ActualWidth), height = (int)Math.Ceiling(element.ActualHeight);
				if (width <= 0 || height <= 0)
					continue;
				var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
				var visual = new DrawingVisual();
				using (var dc = visual.RenderOpen()) {
					dc.DrawRectangle(Brushes.Black, null, new Rect(0, 0, width, height));
					dc.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
				}
				bitmap.Render(visual);
				var encoder = new PngBitmapEncoder();
				encoder.Frames.Add(BitmapFrame.Create(bitmap));
				var file = $"{pathWithoutExtension}-{n++}-{window.GetType().Name}.png";
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				using (var stream = File.Create(file))
					encoder.Save(stream);
				files.Add(file);
			}
			return files;
		}
	}
}
