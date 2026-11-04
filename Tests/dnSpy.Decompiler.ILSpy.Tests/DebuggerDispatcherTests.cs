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

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.Debugger.Shared;
using Xunit;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The debugger engines' dispatcher (the CorDebug and Mono engine threads)
	/// </summary>
	public class DebuggerDispatcherTests {
		/// <summary>
		/// A callback that completes a task (a debug event: a breakpoint hit completes the stepper's task) must return
		/// before the code that awaits the task continues. The stepper's code that continued in the middle of the
		/// breakpoint's debug event continued the process twice: dnSpy then thought the process was running while it
		/// was paused (no call stack after an async step, and a hang on the next step).
		/// </summary>
		[Fact]
		public void TaskCompletedByACallback_ContinuesAfterTheCallback() {
			Dispatcher? dispatcher = null;
			using var created = new ManualResetEventSlim();
			var thread = new Thread(() => {
				dispatcher = new Dispatcher();
				created.Set();
				dispatcher.Run();
			});
			thread.Start();
			created.Wait();

			var log = new List<string>();
			var tcs = new TaskCompletionSource<int>();
			using var gate = new ManualResetEventSlim();
			using var done = new ManualResetEventSlim();
			// The dispatcher runs the callbacks that were queued while it was busy in one go
			dispatcher!.BeginInvoke(() => gate.Wait());
			dispatcher.BeginInvoke(() => Await());
			dispatcher.BeginInvoke(() => {
				tcs.SetResult(0);
				log.Add("callback returns");
			});
			gate.Set();
			Assert.True(done.Wait(10_000));
			dispatcher.BeginInvokeShutdown();
			thread.Join();

			Assert.Equal(new[] { "awaiting", "callback returns", "continuation" }, log);

			async void Await() {
				log.Add("awaiting");
				await tcs.Task;
				log.Add("continuation");
				done.Set();
			}
		}
	}
}
