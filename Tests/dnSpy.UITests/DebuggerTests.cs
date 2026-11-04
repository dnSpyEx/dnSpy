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
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// The debugger on decompiled code: DebuggerPlayground has no PDB, so dnSpy uses the decompiler's debug info.
	/// Each test runs on the Debug and the Release build. See Tests\DebuggerPlayground\README.md for the scenarios.
	/// </summary>
	public sealed class DebuggerTests : UITest {
		public DebuggerTests(DnSpyFixture fixture, ITestOutputHelper log) : base(fixture, log) { }

		const string Program = "DebuggerPlayground.Program";
		const string Scenarios = "DebuggerPlayground.Scenarios";

		string file = "";

		/// <summary>
		/// Starts debugging DebuggerPlayground, paused at the entry point
		/// </summary>
		DebuggerState StartAtEntryPoint(string configuration, string language = "C#") {
			file = Samples.DebuggerPlayground(configuration);
			App.Show(file, Program + "::Main", language);
			var state = App.StartDebuggingAtEntryPoint();
			// The Call Stack window (it's what gets all the frames)
			App.Keys("Ctrl+Alt+C");
			return App.WaitForPause("the call stack");
		}

		/// <summary>
		/// Starts debugging and sets breakpoints in Scenarios: (anchor, statement) pairs
		/// </summary>
		void StartWithBreakpoints(string configuration, params (string anchor, string statement)[] breakpoints) {
			StartAtEntryPoint(configuration);
			App.Show(file, Scenarios);
			foreach (var (anchor, statement) in breakpoints)
				App.ToggleBreakpoint(anchor, statement);
			var bps = App.Call("breakpoints")!.AsArray();
			Log.WriteLine("Breakpoints: " + string.Join(" | ", bps.Select(a => $"{a!["location"]} bound={a["bound"]} {a["message"]}")));
		}

		DebuggerState RunToBreak(string what) => App.Continue() ?? throw new InvalidOperationException("The program exited before: " + what);

		void LogState(string what, DebuggerState state) =>
			Log.WriteLine($"{what}: {state.Tab?.Title} line {state.Tab?.Line}: {state.Tab?.TrimmedLine}  [{state.Frames.FirstOrDefault()?.Text}]");

		static string MethodOf(DebuggerState state) => state.Frames[0].Text;

		public static readonly TheoryData<string> Configurations = new TheoryData<string> { "Debug", "Release" };

		// ---- Entry point (async Main): the compiler's <Main> is shown as Main's declaration

		[Theory]
		[MemberData(nameof(Configurations))]
		public void EntryPoint_AsyncMain_StopsOnMainsDeclaration_StepOverGoesIntoMain(string configuration) {
			var state = StartAtEntryPoint(configuration);
			LogState("entry", state);
			Assert.Contains("<Main>", MethodOf(state));
			Assert.Contains("static async Task Main()", state.Tab!.LineText);

			// F10 doesn't run the whole program: it stops in Main
			state = App.StepOver();
			LogState("F10", state);
			Assert.Contains("Program.Main()", MethodOf(state));
			Assert.True(state.Tab!.TrimmedLine == "{" || state.Tab.LineText.Contains("1 switch"), state.Tab.LineText);

			// And then normal stepping in Main
			state = App.StepOver();
			if (state.Tab!.TrimmedLine == "{")
				state = App.StepOver();
			LogState("F10", state);
			Assert.Contains("Program.Main()", MethodOf(state));
			Assert.Contains("1 switch", state.Tab!.LineText);
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void EntryPoint_AsyncMain_StepIntoGoesIntoMain(string configuration) {
			StartAtEntryPoint(configuration);
			var state = App.StepInto();
			LogState("F11", state);
			Assert.Contains("Program.Main()", MethodOf(state));
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void EntryPoint_InTheILView_StepOverStepsOneInstruction(string configuration) {
			var state = StartAtEntryPoint(configuration, "IL");
			LogState("entry", state);
			Assert.Contains("<Main>", MethodOf(state));
			Assert.Contains("IL_0000", state.Tab!.LineText);
			Assert.Contains("call", state.Tab.LineText);

			state = App.StepOver();
			LogState("F10", state);
			// Over the call of Main (it ran until its first await), still in <Main>
			Assert.Contains("<Main>", MethodOf(state));
			Assert.Contains("IL_0005", state.Tab!.LineText);
		}

		// ---- Breakpoints: each must stop exactly when its statement runs

		[Theory]
		[MemberData(nameof(Configurations))]
		public void Breakpoints_StopOnlyWhenTheirStatementRuns(string configuration) {
			// (method, statement): what's hit, in order. Values from Program.Main.
			var expected = new List<(string method, string statement)> {
				("Switch", "return 10;"),
				("Break", "break;"),
				("Continue", "num += values[i];"), ("Continue", "num += values[i];"),
				("DoWhile", "return n;"),
				("Conditional", "Console.Write(\"\");"), ("Conditional", "Console.Write(\"\");"),
				("Catch", "return ex.Message;"),
				("Catch", "Console.Write(\"\");"), ("Catch", "Console.Write(\"\");"),
				("Captured", "return func(list.Count);"),
				("Resources", "num += text2.Length;"), ("Resources", "num += text2.Length;"),
			};
			// "line1\nline2": the foreach statement's body runs for each of the 11 characters
			for (int i = 0; i < 11; i++)
				expected.Add(("Resources", "num += ((c == '\\n') ? 1 : 0);"));

			StartWithBreakpoints(configuration,
				("Switch(int value)", "return 10;"),
				("Break(IEnumerable<string> names)", "break;"),
				("Continue(int[] values)", "num += values[i];"),
				("DoWhile(int n)", "return n;"),
				("Conditional(string text, int n)", "Console.Write(\"\");"),
				("Catch(string text)", "return ex.Message;"),
				("finally", "Console.Write(\"\");"),
				("Captured(List<int> list)", "return func(list.Count);"),
				("Resources(object gate, string text)", "num += text2.Length;"),
				("Resources(object gate, string text)", "num += ((c == '\\n') ? 1 : 0);"));

			var hits = new List<(string method, string statement)>();
			for (int i = 0; i < expected.Count + 5; i++) {
				var state = App.Continue();
				if (state is null)
					break;
				LogState("hit", state);
				var method = expected.Select(a => a.method).Distinct().FirstOrDefault(m => MethodOf(state).Contains("Scenarios." + m + "(")) ?? MethodOf(state);
				hits.Add((method, state.Tab!.TrimmedLine));
			}
			Assert.Equal(expected.Select(a => $"{a.method}: {a.statement}"), hits.Select(a => $"{a.method}: {a.statement}"));
		}

		// ---- Call stack: a caller's frame shows its call, not the next statement

		[Theory]
		[MemberData(nameof(Configurations))]
		public void CallStack_CallerFrames_ShowTheirCall(string configuration) {
			StartWithBreakpoints(configuration, ("Inner(int n)", "throw new ArgumentException"));
			var state = RunToBreak("Inner");
			LogState("break", state);
			Assert.Contains("Scenarios.Inner(", MethodOf(state));
			foreach (var f in state.Frames)
				Log.WriteLine($"  #{f.Index} {f.Text} @{f.Offset:X}");

			int Find(string text) {
				var frame = state.Frames.FirstOrDefault(a => a.Text.Contains(text, StringComparison.Ordinal));
				Assert.True(frame is not null, "No frame " + text);
				return frame!.Index;
			}

			var lambda = App.SelectFrame(Find("Scenarios.<>c."));
			Log.WriteLine("lambda: " + lambda.TrimmedLine);
			Assert.Contains("Inner(x);", lambda.LineText);

			var middle = App.SelectFrame(Find("Scenarios.Middle("));
			Log.WriteLine("Middle: " + middle.TrimmedLine);
			Assert.Contains(".ForEach(", middle.LineText);

			var outer = App.SelectFrame(Find("Scenarios.Outer("));
			Log.WriteLine("Outer: " + outer.TrimmedLine);
			Assert.Contains("Middle(n + 1);", outer.LineText);

			var main = App.SelectFrame(Find("Program.Main("));
			Log.WriteLine("Main: " + main.TrimmedLine);
			Assert.Contains("Scenarios.Outer(3);", main.LineText);

			// Back to the top frame: the throw
			var top = App.SelectFrame(0);
			Assert.Contains("throw new ArgumentException", top.LineText);
		}

		/// <summary>
		/// A step while an evaluation runs (a tool window's, right after a break) must wait for it, not step in the
		/// evaluated code (it stopped under a [Function Evaluation] frame)
		/// </summary>
		[Theory]
		[MemberData(nameof(Configurations))]
		public void StepDuringAnEvaluation_WaitsForIt(string configuration) {
			StartWithBreakpoints(configuration, ("Outer(int n)", "Middle(n + 1);"));
			RunToBreak("Outer");
			// Like Exception.ToString(): the first stack trace with source info loads assemblies, so the engine handles
			// debug events during the evaluation
			App.Call("evalAsync", new System.Text.Json.Nodes.JsonObject { ["expr"] = "new System.Diagnostics.StackTrace(true).ToString()" });
			System.Threading.Thread.Sleep(30);
			var state = App.StepInto();
			LogState("F11", state);
			foreach (var f in state.Frames.Take(10))
				Log.WriteLine($"  #{f.Index} {f.Text}");
			Assert.DoesNotContain(state.Frames, a => a.Text.Contains("[Function Evaluation]"));
			Assert.Contains("Scenarios.Middle(", MethodOf(state));
		}

				/// <summary>
		/// An evaluation that times out (10 s) and maybe can't be aborted stays on the thread: F11 lets it finish and
		/// steps from the frame where the process stopped, not in the evaluated code
		/// </summary>
		[Fact]
		public void StepAfterATimedOutEvaluation_StepsInTheUsersCode() {
			StartWithBreakpoints("Release", ("Outer(int n)", "Middle(n + 1);"));
			RunToBreak("Outer");
			var (value, error) = App.Evaluate("System.Threading.Thread.Sleep(15000)");
			Log.WriteLine($"Sleep: {value ?? "error " + error}");
			var state = App.StepInto();
			LogState("F11", state);
			Assert.DoesNotContain(state.Frames, a => a.Text.Contains("[Function Evaluation]"));
			Assert.Contains("Scenarios.Middle(", MethodOf(state));
		}

				// ---- Watch and Locals

		[Theory]
		[MemberData(nameof(Configurations))]
		public void Watch_TypesOfTheFilesUsings_Evaluate(string configuration) {
			// (Not on a method's first instruction: there the runtime can't call methods, 0x80131313)
			StartWithBreakpoints(configuration, ("DoWhile(int n)", "return n;"));
			RunToBreak("DoWhile");
			var expressions = new (string expression, string expected)[] {
				("n", "0"),
				("Math.Max(1, 2)", "2"),
				// (dnSpy shows a string with a backslash as a verbatim string)
				("Path.Combine(\"a\", \"b\")", "@\"a\\b\""),
				("new List<int> { 1, 2 }.Count", "2"),
				("string.Join(\",\", new[] { 1, 2 })", "\"1,2\""),
				("Console.Out != null", "true"),
				("EventArgs.Empty == EventArgs.Empty", "true"),
				("typeof(Task).Name", "\"Task\""),
				("System.IO.Path.GetFileName(\"c:\\\\x\\\\y.txt\")", "\"y.txt\""),
			};
			var failures = new List<string>();
			foreach (var (expression, expected) in expressions) {
				var (value, error) = App.Evaluate(expression);
				Log.WriteLine($"{expression} = {value ?? "ERROR " + error}");
				if (value != expected)
					failures.Add($"{expression}: expected {expected}, got {value ?? "error " + error}");
			}
			// A caller's frame: Main
			int main = App.Debugger().Frames.First(a => a.Text.Contains("Program.Main(")).Index;
			var (max, maxError) = App.Evaluate("Math.Max(1, 2)", main);
			if (max != "2")
				failures.Add($"Math.Max(1, 2) in Main: {max ?? maxError}");
			Assert.Empty(failures);
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void Locals_InACatchBlock_HaveTheException(string configuration) {
			StartWithBreakpoints(configuration, ("Catch(string text)", "return ex.Message;"));
			var state = RunToBreak("catch");
			LogState("break", state);
			var locals = App.Locals();
			Log.WriteLine(string.Join(", ", locals.Select(a => $"{a.Key}={a.Value}")));
			Assert.Contains("ex", locals.Keys);
			Assert.Contains("IOException", locals["ex"]);
			var text = App.Evaluate("text");
			Assert.True(text.value == "\"fail\"", $"text = {text.value ?? "error " + text.error}");
			// A field: no call in the debugged process (calling a getter at the start of a catch block can fail, a
			// runtime limit: the thread isn't at a point where it can run code)
			Assert.Equal("\"io\"", App.Evaluate("ex._message").value);
			var message = App.Evaluate("ex.Message");
			Log.WriteLine($"ex.Message = {message.value ?? "ERROR " + message.error}");
		}

		[Fact]
		public void Watch_CapturedVariable_Debug() {
			StartWithBreakpoints("Debug", ("Captured(List<int> list)", "return func(list.Count);"));
			RunToBreak("Captured");
			Assert.Equal("3", App.Evaluate("captured").value);
			Assert.Equal("9", App.Evaluate("func(3)").value);
		}

		// ---- Stepping

		[Theory]
		[MemberData(nameof(Configurations))]
		public void StepOver_Awaits_GoToTheNextStatement(string configuration) {
			StartWithBreakpoints(configuration, ("ComputeAsync(int x)", "int a = await Task.FromResult(x);"));
			var state = RunToBreak("ComputeAsync");
			var lines = new List<string> { state.Tab!.TrimmedLine };
			for (int i = 0; i < 3; i++) {
				state = App.StepOver();
				LogState("F10", state);
				Assert.Contains("ComputeAsync", MethodOf(state));
				lines.Add(state.Tab!.TrimmedLine);
			}
			Assert.Equal(new[] {
				"int a = await Task.FromResult(x);",
				"await Task.Delay(1);",
				"int num = a;",
				"return num + await OtherAsync(a);",
			}, lines);
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void StepOver_LockUsingWhileForeach_VisitsEveryStatement(string configuration) {
			StartWithBreakpoints(configuration, ("Resources(object gate, string text)", "int num = 0;"));
			var state = RunToBreak("Resources");
			var lines = new List<string>();
			for (int i = 0; i < 100 && MethodOf(state).Contains("Scenarios.Resources("); i++) {
				lines.Add(state.Tab!.TrimmedLine);
				state = App.StepOver();
				if (!MethodOf(state).Contains("Scenarios.Resources(")) {
					foreach (var f in state.Frames.Take(12))
						Log.WriteLine($"  #{f.Index} {f.Text} @{f.Offset:X}");
				}
			}
			Log.WriteLine(string.Join("\n", lines));
			LogState("after", state);
			// Back in Main, on the call (or the statement after it)
			Assert.Contains("Program.Main(", MethodOf(state));
			Assert.Contains("Resources", state.Tab!.LineText);

			Assert.Equal(2, lines.Count(a => a == "num += text2.Length;"));
			Assert.Equal(11, lines.Count(a => a == "num += ((c == '\\n') ? 1 : 0);"));
			foreach (var statement in new[] { "int num = 0;", "lock (gate)", "using StringReader stringReader = new StringReader(text);", "return num;" })
				Assert.Contains(statement, lines);
			Assert.Contains(lines, a => a.StartsWith("while ((text2 = stringReader.ReadLine()) != null)", StringComparison.Ordinal));
			Assert.Contains(lines, a => a.StartsWith("foreach (char c in text)", StringComparison.Ordinal) || a == "in" || a == "char c");
			// Never a stop on a line that's only a brace in the middle of the method (nothing runs there)
			Assert.True(lines.Skip(1).Count(a => a == "{") <= 2, string.Join("\n", lines));
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void StepInto_ACallee(string configuration) {
			StartWithBreakpoints(configuration, ("Outer(int n)", "Middle(n + 1);"));
			var state = RunToBreak("Outer");
			state = App.StepInto();
			LogState("F11", state);
			Assert.Contains("Scenarios.Middle(", MethodOf(state));
		}

		[Theory]
		[MemberData(nameof(Configurations))]
		public void StepOut_ReturnsToTheCallersStatement(string configuration) {
			StartWithBreakpoints(configuration, ("Switch(int value)", "return 10;"));
			RunToBreak("Switch");
			var state = App.StepOut();
			LogState("Shift+F11", state);
			Assert.Contains("Program.Main(", MethodOf(state));
			Assert.Contains("1 switch", state.Tab!.LineText);
		}
	}
}
