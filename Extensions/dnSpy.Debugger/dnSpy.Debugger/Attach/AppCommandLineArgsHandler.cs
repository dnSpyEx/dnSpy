/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Attach;

namespace dnSpy.Debugger.Attach {
	[Export(typeof(IAppCommandLineArgsHandler))]
	sealed class AppCommandLineArgsHandler : IAppCommandLineArgsHandler {
		readonly Lazy<AttachableProcessesService> attachableProcessesService;
		readonly Lazy<DbgManager> dbgManager;

		[ImportingConstructor]
		AppCommandLineArgsHandler(Lazy<AttachableProcessesService> attachableProcessesService, Lazy<DbgManager> dbgManager) {
			this.attachableProcessesService = attachableProcessesService;
			this.dbgManager = dbgManager;
		}

		public double Order => 0;

		[DllImport("kernel32.dll")]
		private static extern bool SetEvent(IntPtr hEvent);

		[DllImport("kernel32.dll")]
		private static extern bool CloseHandle(IntPtr hObject);

		private async Task HandleEventOnAttach(AttachableProcess process, IntPtr jitDebugStartEventHandle, bool BreakOnStart) {
			var mgr = dbgManager.Value;
			var tsc = new TaskCompletionSource<object?>();
			Func<Task>? SendSignalEvent = jitDebugStartEventHandle != IntPtr.Zero ? async () => {
				var hdl = jitDebugStartEventHandle;
				jitDebugStartEventHandle = IntPtr.Zero;//we might get called multiple times
				if (hdl != IntPtr.Zero) {
					await Task.Delay(500); //without the delay we will be 15-40 ticks late on attaching, there is no maximum here other than responsiveness for user.  Most of the time without the resume event sent we will not fully attach
					SetEvent(hdl);
					CloseHandle(hdl);
				}
			}
			: null;

			EventHandler<DbgMessageThreadCreatedEventArgs>? threadCreatedHandler = null;

			threadCreatedHandler = async (_, e) => {
				mgr.MessageThreadCreated -= threadCreatedHandler;
				if (BreakOnStart)
					e.Pause = true;
				tsc.TrySetResult(default);

			};
			EventHandler? runningDebuggingHandler = null;
			runningDebuggingHandler = (_, _) => {
				if (!mgr.IsDebugging || mgr.IsRunning != true)
					return;
				SendSignalEvent?.Invoke();
				mgr.IsRunningChanged -= runningDebuggingHandler;
				mgr.IsDebuggingChanged -= runningDebuggingHandler;
			};

			if (jitDebugStartEventHandle != IntPtr.Zero) {
				mgr.IsRunningChanged += runningDebuggingHandler;
				mgr.IsDebuggingChanged += runningDebuggingHandler;
			}

			mgr.MessageThreadCreated += threadCreatedHandler; // even if we are not BreakOnStart we will use this to complete the wait event

			process.Attach();

			/*
				We want to do cleanup here for a few just in cases:
					- If we don't attach we want to remove the event listeners so we don't randomly pause a future session.  
					- if the debug manager status events don't go off as expected and we haven't already sent the event handle signal the process is suspended until the signal comes (or we exit) so it acts as a backup to firing that event.
			*/
			if (await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(10)), tsc.Task) != tsc.Task) {
				SendSignalEvent?.Invoke();
				mgr.MessageThreadCreated -= threadCreatedHandler;
				mgr.IsRunningChanged -= runningDebuggingHandler;
				mgr.IsDebuggingChanged -= runningDebuggingHandler;
			}
		}
		public async void OnNewArgs(IAppCommandLineArgs args) {
			AttachableProcess? process = null;
			if (args.DebugAttachPid is int pid && pid != 0) {
				var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(null, new[] { pid }, null, CancellationToken.None).ConfigureAwait(false);
				process = processes.FirstOrDefault(p => p.ProcessId == pid);
			}
			else if (args.DebugAttachProcess is string processName && !string.IsNullOrEmpty(processName)) {
				var processes = await attachableProcessesService.Value.GetAttachableProcessesAsync(processName, CancellationToken.None).ConfigureAwait(false);
				process = processes.FirstOrDefault();
			}
			if ((args.DebugBreakOnAttach || args.DebugEvent != 0) && process is not null)
				await HandleEventOnAttach(process, new IntPtr(args.DebugEvent), args.DebugBreakOnAttach);
			else
				process?.Attach();
		}

	}
}
