using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;

namespace dnSpy.MainApp {
	/// <summary>
	/// Applies fixes and compatibility workarounds for running on Wine.
	/// </summary>
	static class WineFixes {
		public static void Initialize() {
			if (!IsRunningOnWine())
				return;

			// Disable WPF hardware acceleration on Wine
			RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
		}

		static bool IsRunningOnWine() {
			var ntdll = GetModuleHandle("ntdll.dll");
			if (ntdll == IntPtr.Zero)
				return false;
			return GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
		}

		[DllImport("kernel32", SetLastError = true)]
		static extern IntPtr GetModuleHandle(string lpModuleName);

		[DllImport("kernel32", SetLastError = true)]
		static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);
	}
}
