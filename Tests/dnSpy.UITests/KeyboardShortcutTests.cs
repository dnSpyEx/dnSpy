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
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// Only runs when DNSPY_UITEST_REAL_KEYBOARD=1: these tests use the real keyboard, so dnSpy is on the screen and
	/// must keep the focus (don't use the PC while they run)
	/// </summary>
	public sealed class RealKeyboardFactAttribute : FactAttribute {
		public RealKeyboardFactAttribute() {
			if (Environment.GetEnvironmentVariable("DNSPY_UITEST_REAL_KEYBOARD") != "1")
				Skip = "Uses the real keyboard: set DNSPY_UITEST_REAL_KEYBOARD=1 (and don't use the PC while it runs)";
		}
	}

	public sealed class RealKeyboardFixture : IDisposable {
		DnSpyApp? app;

		internal DnSpyApp App {
			get {
				if (app is null || app.HasExited) {
					app?.Dispose();
					app = DnSpyApp.Start(realKeyboard: true);
				}
				return app;
			}
		}

		public void Dispose() => app?.Dispose();
	}

	/// <summary>
	/// dnSpy's keyboard shortcuts with the real keyboard (Windows' input, WPF's focus). The other tests press the same
	/// keys through the probe; these check that the real keys get there.
	/// </summary>
	[Collection(DnSpyCollection.Name)]
	public sealed class KeyboardShortcutTests : IClassFixture<RealKeyboardFixture>, IDisposable {
		readonly ITestOutputHelper log;
		readonly DnSpyApp app;
		RealKeyboard Keyboard => app.RealKeyboard!;

		public KeyboardShortcutTests(RealKeyboardFixture fixture, ITestOutputHelper log) {
			this.log = log;
			if (Environment.GetEnvironmentVariable("DNSPY_UITEST_REAL_KEYBOARD") != "1") {
				app = null!;
				return;
			}
			app = fixture.App;
			app.Reset();
		}

		public void Dispose() {
			if (app is null)
				return;
			var problems = app.TakeProblems();
			app.Reset();
			Assert.True(problems.Count == 0, string.Join("\n", problems));
		}

		void LogFrames(string what, DebuggerState state) {
			log.WriteLine($"{what}: pauses={state.Pauses} tab={state.Tab?.Title} line={state.Tab?.TrimmedLine}");
			foreach (var f in state.Frames.Take(15))
				log.WriteLine($"  #{f.Index} {f.Text} @{f.Offset:X}");
		}

		[RealKeyboardFact]
		public void CtrlShiftE_OpensEditMethod() {
			app.Show(Samples.ConsoleSample(), "ConsoleSample.Program::Sum", "C#");
			Keyboard.Press("Ctrl+Shift+E");
			app.WaitForDialog("EditCodeDlg");
			app.WaitUntil(() => app.EditCode().HasDecompiled, "the code");
			// (Escape doesn't close Edit Code: the code editor has the keyboard, the Cancel button isn't IsCancel)
			app.Click("EditCodeDlg", "Cancel");
			app.WaitForDialogClosed("EditCodeDlg");
		}

		[RealKeyboardFact]
		public void DebuggerKeys_F11_Escape_Enter_F9_F5_ShiftF11_F10_CtrlAltC_ShiftF5() {
			var file = Samples.DebuggerPlayground("Release");
			app.Show(file, "DebuggerPlayground.Program::Main", "C#");

			// F11: Debug Program dialog, Escape: Cancel, F11 again, Enter: OK
			Keyboard.Press("F11");
			app.WaitForDialog("DebugProgramDlg");
			Keyboard.Press("Escape");
			app.WaitForDialogClosed("DebugProgramDlg");
			Assert.False(app.Debugger().IsDebugging);
			Keyboard.Press("F11");
			app.WaitForDialog("DebugProgramDlg");
			Keyboard.Press("Enter");
			var state = app.WaitForPause("the entry point");
			Assert.Contains("<Main>", state.Frames[0].Text);

			// F9: breakpoints
			app.Show(file, "DebuggerPlayground.Scenarios");
			app.CaretTo("Switch(int value)", "return 10;");
			Keyboard.Press("F9");
			app.CaretTo("Outer(int n)", "Middle(n + 1);");
			Keyboard.Press("F9");
			app.WaitUntil(() => app.Call("breakpoints")!.AsArray().Count == 2, "the breakpoints");

			// F5: runs to Switch, Shift+F11: back in Main
			state = Press("F5");
			Assert.Contains("Scenarios.Switch(", state.Frames[0].Text);
			state = Press("Shift+F11");
			Assert.Contains("Program.Main(", state.Frames[0].Text);
			Assert.Contains("1 switch", state.Tab!.LineText);

			// F5: runs to Outer, F11: into Middle, F10: over the throwing ForEach (to Main's catch)
			state = Press("F5");
			Assert.Contains("Scenarios.Outer(", state.Frames[0].Text);
			state = Press("F11");
			Assert.Contains("Scenarios.Middle(", state.Frames[0].Text);
			state = Press("F10");
			Assert.DoesNotContain("[Function Evaluation]", string.Join("\n", state.Frames.Select(a => a.Text)));

			// Ctrl+Alt+C: the Call Stack window (all the frames)
			Keyboard.Press("Ctrl+Alt+C");
			app.WaitUntil(() => app.Debugger().Frames.Count > 1, "the call stack");

			// Shift+F5: stop
			Keyboard.Press("Shift+F5");
			app.WaitUntil(() => !app.Debugger().IsDebugging, "debugging to stop");
		}

		/// <summary>
		/// Presses a key that runs the process and waits for the next pause, then for the tool windows to finish
		/// evaluating (see <see cref="StepRightAfterABreak_DoesntStepInAFunctionEvaluation"/>)
		/// </summary>
		DebuggerState Press(string keys, bool settle = true) {
			int pauses = app.Debugger().Pauses;
			Keyboard.Press(keys);
			app.WaitUntil(() => app.Debugger().Pauses > pauses, keys);
			var state = app.WaitForPause(keys);
			LogFrames(keys, state);
			if (settle)
				System.Threading.Thread.Sleep(2000);
			return state;
		}

		/// <summary>
		/// After a break, the visible Locals window evaluates (e.g. $exception.ToString(), which loads assemblies): that
		/// evaluation timed out (the engine paused for the module loads and waited for DbgManager, which waited for the
		/// evaluation), stayed on the thread, and the step stopped in the evaluated code, under [Function Evaluation].
		/// (DebuggerTests.StepDuringAnEvaluation_WaitsForIt tests it without the real keyboard.)
		/// </summary>
		[RealKeyboardFact]
		public void StepRightAfterABreak_DoesntStepInAFunctionEvaluation() {
			var file = Samples.DebuggerPlayground("Release");
			app.Show(file, "DebuggerPlayground.Program::Main", "C#");
			Keyboard.Press("F11");
			app.WaitForDialog("DebugProgramDlg");
			Keyboard.Press("Enter");
			app.WaitForPause("the entry point");
			app.Show(file, "DebuggerPlayground.Scenarios");
			app.CaretTo("Outer(int n)", "Middle(n + 1);");
			Keyboard.Press("F9");
			app.WaitUntil(() => app.Call("breakpoints")!.AsArray().Count == 1, "the breakpoint");
			var state = Press("F5", settle: false);
			Assert.Contains("Scenarios.Outer(", state.Frames[0].Text);
			// At once: F11
			state = Press("F11", settle: false);
			Assert.DoesNotContain("[Function Evaluation]", string.Join("\n", state.Frames.Select(a => a.Text)));
			Assert.Contains("Scenarios.Middle(", state.Frames[0].Text);
			Keyboard.Press("Shift+F5");
			app.WaitUntil(() => !app.Debugger().IsDebugging, "debugging to stop");
		}

		[RealKeyboardFact]
		public void F12_GoesToTheDefinition() {
			app.Show(Samples.ConsoleSample(), "ConsoleSample.Program::Main", "C#");
			app.CaretTo("Describe(", delta: 1);
			Keyboard.Press("F12");
			app.WaitUntil(() => app.Tab().LineText.Contains("Describe(object o)"), "Describe's definition");
		}
	}
}
