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
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using FlaUI.Core;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// The real keyboard (SendInput, through Windows): only for the keyboard shortcut tests, which need dnSpy in the
	/// foreground of the user's desktop
	/// </summary>
	sealed class RealKeyboard : IDisposable {
		readonly int processId;
		readonly UIA3Automation automation;
		readonly Application application;
		readonly Window mainWindow;

		public RealKeyboard(int processId) {
			this.processId = processId;
			automation = new UIA3Automation();
			application = Application.Attach(processId);
			mainWindow = application.GetMainWindow(automation, DnSpyApp.DefaultTimeout) ?? throw new TimeoutException("dnSpy's main window didn't appear");
		}

		[DllImport("user32.dll")]
		static extern IntPtr GetForegroundWindow();
		[DllImport("user32.dll")]
		static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

		bool IsForeground() {
			GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
			return pid == (uint)processId;
		}

		/// <summary>
		/// Brings dnSpy (its dialog if one is open) to the foreground
		/// </summary>
		public void Activate() {
			for (int i = 0; i < 20; i++) {
				Window? dialog = null;
				try {
					dialog = mainWindow.ModalWindows.LastOrDefault();
				}
				catch {
				}
				var target = dialog ?? mainWindow;
				if (IsForeground() && (dialog is null || GetForegroundWindow() == dialog.Properties.NativeWindowHandle.ValueOrDefault))
					return;
				try {
					target.SetForeground();
					target.Focus();
				}
				catch {
				}
				Thread.Sleep(100);
			}
			if (!IsForeground())
				throw new InvalidOperationException("Couldn't bring dnSpy to the foreground (don't use the mouse or keyboard while the keyboard tests run)");
		}

		/// <summary>
		/// Presses keys together with the real keyboard: "Ctrl+Shift+E", "F10"
		/// </summary>
		public void Press(string keys) {
			var parts = keys.Split('+').Select(a => a.Trim()).ToArray();
			var vks = parts.Select(ToVirtualKey).ToArray();
			Activate();
			Keyboard.TypeSimultaneously(vks);
			Thread.Sleep(100);
		}

		static VirtualKeyShort ToVirtualKey(string name) {
			switch (name.ToLowerInvariant()) {
			case "ctrl": case "control": return VirtualKeyShort.CONTROL;
			case "shift": return VirtualKeyShort.SHIFT;
			case "alt": return VirtualKeyShort.ALT;
			case "escape": case "esc": return VirtualKeyShort.ESCAPE;
			case "enter": case "return": return VirtualKeyShort.ENTER;
			case "delete": return VirtualKeyShort.DELETE;
			case "tab": return VirtualKeyShort.TAB;
			}
			if (name.Length == 1 && char.IsLetterOrDigit(name[0]))
				return (VirtualKeyShort)char.ToUpperInvariant(name[0]);
			if (Enum.TryParse<VirtualKeyShort>(name, true, out var vk))
				return vk;
			throw new ArgumentException("Unknown key " + name);
		}

		public void Dispose() {
			application.Dispose();
			automation.Dispose();
		}
	}
}
