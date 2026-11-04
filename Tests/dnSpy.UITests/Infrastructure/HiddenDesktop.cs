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
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// A Windows desktop nobody sees: dnSpy, its dialogs and the programs it debugs run there, so they never show on
	/// the user's screen or take the focus (the tests use the probe for input, not the real keyboard)
	/// </summary>
	static class HiddenDesktop {
		const uint DESKTOP_ALL_ACCESS = 0x01FF;
		const uint GENERIC_ALL = 0x10000000;
		const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
		const int STARTF_USESHOWWINDOW = 0x00000001;
		const short SW_SHOWMAXIMIZED = 3;

		[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
		struct STARTUPINFO {
			public int cb;
			public string? lpReserved;
			public string? lpDesktop;
			public string? lpTitle;
			public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
			public short wShowWindow, cbReserved2;
			public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct PROCESS_INFORMATION {
			public IntPtr hProcess, hThread;
			public int dwProcessId, dwThreadId;
		}

		[DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateDesktopW(string lpszDesktop, IntPtr lpszDevice, IntPtr pDevmode, int dwFlags, uint dwDesiredAccess, IntPtr lpsa);

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern bool CreateProcessW(string? lpApplicationName, StringBuilder lpCommandLine, IntPtr lpProcessAttributes, IntPtr lpThreadAttributes,
			bool bInheritHandles, uint dwCreationFlags, string lpEnvironment, string lpCurrentDirectory, ref STARTUPINFO lpStartupInfo, out PROCESS_INFORMATION lpProcessInformation);

		[DllImport("kernel32.dll")]
		static extern bool CloseHandle(IntPtr handle);

		[DllImport("kernel32.dll")]
		static extern uint ResumeThread(IntPtr hThread);

		const uint CREATE_SUSPENDED = 0x00000004;

		static readonly Lazy<string> desktopName = new Lazy<string>(Create);
		static IntPtr desktopHandle;

		static string Create() {
			// One per test run; it goes away when the last handle and process on it are gone
			var name = "dnSpy.UITests." + Environment.ProcessId;
			desktopHandle = CreateDesktopW(name, IntPtr.Zero, IntPtr.Zero, 0, DESKTOP_ALL_ACCESS | GENERIC_ALL, IntPtr.Zero);
			if (desktopHandle == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateDesktop failed");
			return name;
		}

		/// <summary>
		/// Starts a program on the hidden desktop. It inherits no handles (so 'dotnet test' doesn't wait for it).
		/// </summary>
		public static Process Start(string exe, IEnumerable<string> args, string workingDirectory, IDictionary<string, string> extraEnvironment) {
			var commandLine = new StringBuilder(Quote(exe));
			foreach (var a in args)
				commandLine.Append(' ').Append(Quote(a));

			var env = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
				env[(string)e.Key] = (string?)e.Value ?? "";
			foreach (var kv in extraEnvironment)
				env[kv.Key] = kv.Value;
			var block = new StringBuilder();
			foreach (var kv in env)
				block.Append(kv.Key).Append('=').Append(kv.Value).Append('\0');
			block.Append('\0');

			var si = new STARTUPINFO {
				cb = Marshal.SizeOf<STARTUPINFO>(),
				lpDesktop = @"WinSta0\" + desktopName.Value,
				dwFlags = STARTF_USESHOWWINDOW,
				wShowWindow = SW_SHOWMAXIMIZED,
			};
			if (!CreateProcessW(null, commandLine, IntPtr.Zero, IntPtr.Zero, false, CREATE_UNICODE_ENVIRONMENT | CREATE_SUSPENDED, block.ToString(), workingDirectory, ref si, out var pi))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateProcess failed: " + exe);
			try {
				// Before it runs: the programs it debugs are in the job too
				KillOnExitJob.Add(pi.hProcess);
				var process = Process.GetProcessById(pi.dwProcessId);
				ResumeThread(pi.hThread);
				return process;
			}
			finally {
				CloseHandle(pi.hThread);
				CloseHandle(pi.hProcess);
			}
		}

		static string Quote(string s) {
			if (s.Length != 0 && !s.Any(c => char.IsWhiteSpace(c) || c == '"'))
				return s;
			var sb = new StringBuilder("\"");
			int backslashes = 0;
			foreach (var c in s) {
				if (c == '\\') {
					backslashes++;
					continue;
				}
				if (c == '"') {
					sb.Append('\\', backslashes * 2 + 1).Append('"');
				}
				else {
					sb.Append('\\', backslashes).Append(c);
				}
				backslashes = 0;
			}
			sb.Append('\\', backslashes * 2).Append('"');
			return sb.ToString();
		}
	}
}
