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
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace dnSpy.UITests.Infrastructure {
	sealed record TabState(string Title, string Text, int Caret, int Line, string LineText, bool Busy, string Language) {
		public static TabState From(JsonNode n) => new TabState((string?)n["title"] ?? "", (string?)n["text"] ?? "", (int?)n["caret"] ?? -1,
			(int?)n["line"] ?? -1, (string?)n["lineText"] ?? "", (bool?)n["busy"] ?? false, (string?)n["language"] ?? "");

		public string TrimmedLine => LineText.Trim();
	}

	sealed record Diagnostic(string Code, string Description, string File, string Line) {
		public override string ToString() => $"{Code} {Description} [{File}:{Line}]";
	}

	sealed record EditCodeState(bool HasDecompiled, bool CanCompile, List<Diagnostic> Diagnostics, Dictionary<string, string> Documents);

	sealed record Frame(int Index, string Text, string Token, uint Offset, string Module);

	sealed record DebuggerState(bool IsDebugging, bool? IsRunning, string? ProcessState, int ActiveFrame, List<Frame> Frames, TabState? Tab, int Pauses) {
		public bool IsPaused => ProcessState == "Paused";
	}

	/// <summary>
	/// What a user does in dnSpy, done with the keyboard and the UI where it matters
	/// </summary>
	static class Actions {
		// ---- Code view

		public static TabState Tab(this DnSpyApp app) => TabState.From(app.Call("tab")!);

		public static void SetLanguage(this DnSpyApp app, string language) {
			app.Call("setLanguage", new JsonObject { ["name"] = language });
			app.Idle();
		}

		public static void Open(this DnSpyApp app, string file) {
			if (app.Call("documents")!.AsArray().Any(a => string.Equals((string?)a, file, StringComparison.OrdinalIgnoreCase)))
				return;
			app.Call("open", new JsonObject { ["path"] = file });
			app.Idle();
		}

		/// <summary>
		/// Goes to a member (see the probe's member syntax: token, "NS.Type", "NS.Type::Name#n") and waits for the code
		/// </summary>
		public static TabState Show(this DnSpyApp app, string file, string member, string? language = null) {
			if (language is not null && app.Tab().Language != language)
				app.SetLanguage(language);
			app.Open(file);
			var result = app.Call("follow", new JsonObject { ["path"] = file, ["member"] = member })!;
			return app.WaitForCode("code of " + (string?)result["fullName"]);
		}

		/// <summary>
		/// Waits until the active tab has decompiled and its text stopped changing
		/// </summary>
		public static TabState WaitForCode(this DnSpyApp app, string what = "the code", TimeSpan? timeout = null) {
			TabState? last = null;
			app.WaitUntil(() => {
				app.Idle();
				var tab = app.Tab();
				bool stable = !tab.Busy && tab.Text.Length != 0 && last is not null && last.Text == tab.Text && last.Title == tab.Title;
				last = tab;
				return stable;
			}, what, timeout ?? TimeSpan.FromMinutes(2));
			return last!;
		}

		public static TabState CaretTo(this DnSpyApp app, string text, int nth = 1, int delta = 0) {
			app.Call("caretTo", new JsonObject { ["text"] = text, ["nth"] = nth, ["delta"] = delta });
			app.Idle();
			return app.Tab();
		}

		/// <summary>
		/// Puts the caret on the first <paramref name="text"/> after <paramref name="anchor"/> (e.g. a method's signature)
		/// </summary>
		public static TabState CaretTo(this DnSpyApp app, string anchor, string text) {
			var all = app.Tab().Text;
			int start = all.IndexOf(anchor, StringComparison.Ordinal);
			if (start < 0)
				throw new InvalidOperationException($"'{anchor}' isn't in the active tab");
			int index = all.IndexOf(text, start, StringComparison.Ordinal);
			if (index < 0)
				throw new InvalidOperationException($"'{text}' isn't after '{anchor}' in the active tab");
			int nth = 0;
			for (int i = -1; (i = all.IndexOf(text, i + 1, StringComparison.Ordinal)) >= 0 && i <= index;)
				nth++;
			return app.CaretTo(text, nth);
		}

		// ---- Edit code

		public static EditCodeState EditCode(this DnSpyApp app) {
			var n = app.Call("editCode")!;
			return new EditCodeState((bool)n["hasDecompiled"]!, (bool)n["canCompile"]!,
				n["diagnostics"]!.AsArray().Select(d => new Diagnostic((string?)d!["code"] ?? "", (string?)d["description"] ?? "", (string?)d["file"] ?? "", (string?)d["line"] ?? "")).ToList(),
				n["documents"]!.AsArray().ToDictionary(d => (string)d!["name"]!, d => (string)d!["text"]!));
		}

		const string EditCodeDialog = ".EditCodeDlg";

		/// <summary>
		/// Edit Method (C#) on the member at the caret (Ctrl+Shift+E), and waits for the code
		/// </summary>
		public static EditCodeState OpenEditMethod(this DnSpyApp app) =>
			app.TryOpenEditMethod(DnSpyApp.DefaultTimeout) ?? throw new TimeoutException("Edit Method (Ctrl+Shift+E) didn't open the Edit Code dialog");

		/// <summary>
		/// Ctrl+Shift+E: null if no Edit Code dialog opened (no method at the caret)
		/// </summary>
		public static EditCodeState? TryOpenEditMethod(this DnSpyApp app, TimeSpan timeout) {
			app.Keys("Ctrl+Shift+E");
			var watch = System.Diagnostics.Stopwatch.StartNew();
			while (!app.Dialogs().Any(a => a.type.EndsWith(EditCodeDialog, StringComparison.Ordinal))) {
				if (watch.Elapsed > timeout)
					return null;
				Thread.Sleep(100);
			}
			EditCodeState? state = null;
			app.WaitUntil(() => (state = app.EditCode()).HasDecompiled && state.CanCompile, "the Edit Code dialog to decompile", TimeSpan.FromMinutes(2));
			return state!;
		}

		/// <summary>
		/// Clicks Compile. Returns the errors (the dialog stays open) or an empty list (it compiled, imported and closed).
		/// </summary>
		public static List<Diagnostic> Compile(this DnSpyApp app) {
			app.WaitForDialog(EditCodeDialog);
			app.Click(EditCodeDialog, "Compile");
			List<Diagnostic>? errors = null;
			app.WaitUntil(() => {
				if (!app.Dialogs().Any(a => a.type.EndsWith(EditCodeDialog, StringComparison.Ordinal))) {
					errors = new List<Diagnostic>();
					return true;
				}
				var state = app.EditCode();
				if (state.CanCompile && state.Diagnostics.Count != 0) {
					errors = state.Diagnostics;
					return true;
				}
				return false;
			}, "Compile to finish", TimeSpan.FromMinutes(3));
			app.Idle();
			return errors!;
		}

		public static void CancelEditCode(this DnSpyApp app) {
			app.WaitForDialog(EditCodeDialog);
			app.Click(EditCodeDialog, "Cancel");
			app.WaitForDialogClosed(EditCodeDialog);
		}

		public static void ReplaceInEditCode(this DnSpyApp app, string find, string replace, string document = "main.cs") =>
			app.Call("editCodeReplace", new JsonObject { ["find"] = find, ["replace"] = replace, ["document"] = document });

		/// <summary>
		/// Save All (Ctrl+Shift+S) with one modified module: saves it as <paramref name="file"/>
		/// </summary>
		public static void SaveModuleAs(this DnSpyApp app, string file) {
			app.Keys("Ctrl+Shift+S");
			app.WaitForDialog("SaveModuleOptionsDlg");
			app.SetText("SaveModuleOptionsDlg", "filenameTextBox", file);
			app.Click("SaveModuleOptionsDlg", "OK");
			app.WaitForDialogClosed("SaveModuleOptionsDlg");
			// Saving shows a progress dialog
			app.WaitUntil(() => app.Dialogs().Count == 0, "saving to finish", TimeSpan.FromMinutes(2));
			app.WaitUntil(() => System.IO.File.Exists(file), "the saved file");
		}

		// ---- Debugger

		public static DebuggerState Debugger(this DnSpyApp app) {
			var n = app.Call("dbg")!;
			var frames = n["frames"]!.AsArray().Select(f => new Frame((int)f!["index"]!, (string?)f["text"] ?? "", (string)f["token"]!, (uint)f["offset"]!, (string?)f["module"] ?? "")).ToList();
			return new DebuggerState((bool)n["isDebugging"]!, (bool?)n["isRunning"], (string?)n["processState"], (int)n["activeFrame"]!, frames,
				n["tab"] is JsonNode tab ? TabState.From(tab) : null, (int)n["pauses"]!);
		}

		/// <summary>
		/// F11 with the program selected: Debug Program dialog (break at the entry point), OK, and waits for the break
		/// </summary>
		public static DebuggerState StartDebuggingAtEntryPoint(this DnSpyApp app) {
			int pauses = app.Debugger().Pauses;
			app.Keys("F11");
			app.WaitForDialog("DebugProgramDlg");
			app.Click("DebugProgramDlg", "OK");
			app.WaitForDialogClosed("DebugProgramDlg");
			app.WaitUntil(() => app.Debugger().Pauses > pauses, "the break at the entry point");
			return app.WaitForPause("the break at the entry point");
		}

		/// <summary>
		/// Waits until the process is paused and dnSpy has shown the current statement (the caret is on it)
		/// </summary>
		public static DebuggerState WaitForPause(this DnSpyApp app, string what, TimeSpan? timeout = null) {
			DebuggerState? last = null;
			app.WaitUntil(() => {
				app.Idle();
				var state = app.Debugger();
				if (!state.IsDebugging)
					throw new InvalidOperationException($"The debugged program exited while waiting for: {what}");
				bool stable = state.IsPaused && state.IsRunning == false && state.Frames.Count != 0 && state.Tab is not null && !state.Tab.Busy &&
					last is not null && last.Tab?.Caret == state.Tab.Caret && last.Tab.Title == state.Tab.Title && last.ActiveFrame == state.ActiveFrame &&
					last.Pauses == state.Pauses && last.Frames.SequenceEqual(state.Frames);
				last = state;
				return stable;
			}, what, timeout ?? TimeSpan.FromMinutes(2));
			return last!;
		}

		/// <summary>
		/// Presses a stepping key (F10, F11, Shift+F11) or F5 and waits for the next pause
		/// </summary>
		public static DebuggerState Step(this DnSpyApp app, string keys) =>
			app.RunUntilPause(keys, "step " + keys) ?? throw new InvalidOperationException("The program exited while stepping");

		/// <summary>
		/// Presses keys that run the process, and waits for the next pause (null: the program exited)
		/// </summary>
		static DebuggerState? RunUntilPause(this DnSpyApp app, string keys, string what, TimeSpan? timeout = null) {
			int pauses = app.Debugger().Pauses;
			app.Keys(keys);
			bool exited = false;
			app.WaitUntil(() => {
				var s = app.Debugger();
				exited = !s.IsDebugging;
				return exited || s.Pauses > pauses;
			}, what, timeout ?? TimeSpan.FromMinutes(2));
			return exited ? null : app.WaitForPause(what);
		}

		public static DebuggerState StepOver(this DnSpyApp app) => app.Step("F10");
		public static DebuggerState StepInto(this DnSpyApp app) => app.Step("F11");
		public static DebuggerState StepOut(this DnSpyApp app) => app.Step("Shift+F11");

		/// <summary>
		/// F5: runs to the next break, or returns null if the program exits
		/// </summary>
		public static DebuggerState? Continue(this DnSpyApp app, TimeSpan? timeout = null) =>
			app.RunUntilPause("F5", "F5 to break or exit", timeout);

		/// <summary>
		/// Toggles a breakpoint (F9) on the statement that starts with <paramref name="text"/> in the active tab
		/// </summary>
		public static void ToggleBreakpoint(this DnSpyApp app, string anchor, string text) {
			app.CaretTo(anchor, text);
			int count = app.Call("breakpoints")!.AsArray().Count;
			app.Keys("F9");
			app.WaitUntil(() => app.Call("breakpoints")!.AsArray().Count != count, $"the breakpoint on '{text}'", TimeSpan.FromSeconds(10));
		}

		/// <summary>
		/// Selects a call stack frame (like double-clicking it in the Call Stack window) and returns where the caret went
		/// </summary>
		public static TabState SelectFrame(this DnSpyApp app, int index) {
			var before = app.Debugger();
			app.Call("setFrame", new JsonObject { ["index"] = index });
			// (It goes to the frame later, maybe after decompiling another method)
			var watch = System.Diagnostics.Stopwatch.StartNew();
			while (watch.Elapsed < TimeSpan.FromSeconds(5)) {
				var now = app.Debugger();
				if (now.ActiveFrame == index && (now.Tab?.Caret != before.Tab?.Caret || now.Tab?.Title != before.Tab?.Title))
					break;
				Thread.Sleep(100);
			}
			return app.WaitForPause($"frame {index} to be shown").Tab!;
		}

		public static (string? value, string? error) Evaluate(this DnSpyApp app, string expression, int? frame = null) {
			var args = new JsonObject { ["expr"] = expression };
			if (frame is not null)
				args["frame"] = frame;
			var n = app.Call("eval", args)!;
			return ((string?)n["value"], (string?)n["error"]);
		}

		public static Dictionary<string, string?> Locals(this DnSpyApp app, int? frame = null) {
			var args = new JsonObject();
			if (frame is not null)
				args["frame"] = frame;
			return app.Call("locals", args)!.AsArray().GroupBy(a => (string)a!["name"]!).ToDictionary(g => g.Key, g => (string?)g.First()!["value"] ?? (string?)g.First()!["error"]);
		}
	}
}
