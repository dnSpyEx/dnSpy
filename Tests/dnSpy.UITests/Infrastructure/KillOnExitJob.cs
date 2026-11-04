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
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// A job object that Windows closes when the test host exits, however it exits: the dnSpy processes in it (and the
	/// programs they debug) are killed with it, so nothing is left running on the hidden desktop
	/// </summary>
	static class KillOnExitJob {
		const int JobObjectExtendedLimitInformation = 9;
		const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

		[StructLayout(LayoutKind.Sequential)]
		struct JOBOBJECT_BASIC_LIMIT_INFORMATION {
			public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
			public uint LimitFlags;
			public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
			public uint ActiveProcessLimit;
			public UIntPtr Affinity;
			public uint PriorityClass, SchedulingClass;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct IO_COUNTERS {
			public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
		}

		[StructLayout(LayoutKind.Sequential)]
		struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION {
			public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
			public IO_COUNTERS IoInfo;
			public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
		}

		[DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
		static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool SetInformationJobObject(IntPtr hJob, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int cbInfo);

		[DllImport("kernel32.dll", SetLastError = true)]
		static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

		// Never closed: Windows closes it when the test host exits
		static readonly Lazy<IntPtr> job = new Lazy<IntPtr>(() => {
			var handle = CreateJobObjectW(IntPtr.Zero, null);
			if (handle == IntPtr.Zero)
				throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed");
			var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
			info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
			if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "SetInformationJobObject failed");
			return handle;
		});

		public static void Add(IntPtr processHandle) {
			if (!AssignProcessToJobObject(job.Value, processHandle))
				throw new Win32Exception(Marshal.GetLastWin32Error(), "AssignProcessToJobObject failed");
		}
	}
}
