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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// A dnSpy process started for the tests, with its own settings file (nothing from the user's dnSpy) and the probe
	/// extension. By default it runs on a hidden desktop and all input goes through the probe (WPF key events and
	/// automation peers inside dnSpy): the tests don't need the focus and the PC can be used while they run.
	/// <see cref="RealKeyboard"/> (KeyboardShortcutTests) runs it on the user's desktop and uses the real keyboard.
	/// </summary>
	sealed class DnSpyApp : IDisposable {
		public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);

		readonly Process process;
		public ProbeClient Probe { get; }
		public string WorkDirectory { get; }
		/// <summary>
		/// Only when started with the real keyboard
		/// </summary>
		public RealKeyboard? RealKeyboard { get; }

		DnSpyApp(Process process, ProbeClient probe, string workDirectory, bool realKeyboard) {
			this.process = process;
			Probe = probe;
			WorkDirectory = workDirectory;
			if (realKeyboard)
				RealKeyboard = new RealKeyboard(process.Id);
		}

		public static string DnSpyExe {
			get {
				var exe = Environment.GetEnvironmentVariable("DNSPY_UITEST_EXE");
				if (!string.IsNullOrEmpty(exe))
					return exe;
#if DEBUG
				const string configuration = "Debug";
#else
				const string configuration = "Release";
#endif
				return Path.Combine(Samples.RepoRoot, "dnSpy", "dnSpy", "bin", configuration, "net10.0-windows", "dnSpy.exe");
			}
		}

		/// <param name="realKeyboard">false: on a hidden desktop, input through the probe. true: on the user's desktop,
		/// for tests of the real keyboard.</param>
		public static DnSpyApp Start(bool realKeyboard = false) {
			var exe = DnSpyExe;
			if (!File.Exists(exe))
				throw new FileNotFoundException("Build dnSpy first (or set DNSPY_UITEST_EXE): " + exe);
			var probeDir = Path.Combine(AppContext.BaseDirectory, "Probe");
			var work = Samples.NewWorkDirectory("dnSpy");
			var pipeName = "dnSpy.UITests." + Guid.NewGuid().ToString("N");
			var args = new[] { "--multiple", "--settings-file", Path.Combine(work, "dnSpy.xml"), "--extension-directory", probeDir,
				"--no-load-files", "--not-full-screen" };
			var env = new Dictionary<string, string> { ["DNSPY_UITEST_PIPE"] = pipeName };

			Process process;
			if (realKeyboard) {
				var info = new ProcessStartInfo(exe) {
					UseShellExecute = false,
					WorkingDirectory = work,
					// Else dnSpy inherits the test host's output handles and 'dotnet test' waits for dnSpy to exit
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					RedirectStandardInput = true,
				};
				foreach (var a in args)
					info.ArgumentList.Add(a);
				foreach (var kv in env)
					info.Environment[kv.Key] = kv.Value;
				process = Process.Start(info)!;
				KillOnExitJob.Add(process.Handle);
				process.OutputDataReceived += (s, e) => { };
				process.ErrorDataReceived += (s, e) => { };
				process.BeginOutputReadLine();
				process.BeginErrorReadLine();
			}
			else
				process = HiddenDesktop.Start(exe, args, work, env);

			try {
				var probe = ProbeClient.Connect(pipeName, process, TimeSpan.FromSeconds(120));
				probe.Call("ping");
				var app = new DnSpyApp(process, probe, work, realKeyboard);
				app.WaitUntil(() => app.Call("windows")!.AsArray().Any(a => (bool)a!["isMain"]!), "dnSpy's main window");
				return app;
			}
			catch {
				try { process.Kill(true); } catch { }
				throw;
			}
		}

		public JsonNode? Call(string cmd, JsonObject? args = null) => Probe.Call(cmd, args);

		// ---- Waiting

		public void WaitUntil(Func<bool> condition, string what, TimeSpan? timeout = null) {
			var watch = Stopwatch.StartNew();
			var limit = timeout ?? DefaultTimeout;
			Exception? last = null;
			while (true) {
				try {
					if (condition())
						return;
					last = null;
				}
				catch (ProbeCallException ex) {
					last = ex;
				}
				if (process.HasExited)
					throw new InvalidOperationException($"dnSpy exited (code {process.ExitCode}) while waiting for: {what}");
				if (watch.Elapsed > limit)
					throw new TimeoutException($"Timed out after {limit.TotalSeconds:F0} s waiting for: {what}{(last is null ? "" : " (" + last.Message + ")")}. Screenshots: {Screenshot(what)}");
				Thread.Sleep(100);
			}
		}

		public T WaitFor<T>(Func<T?> get, string what, TimeSpan? timeout = null) where T : class {
			T? value = null;
			WaitUntil(() => (value = get()) is not null, what, timeout);
			return value!;
		}

		/// <summary>
		/// Waits until the UI thread has nothing else to do (twice: work that was queued by the first idle)
		/// </summary>
		public void Idle() {
			Call("idle");
			Call("idle");
		}

		/// <summary>
		/// dnSpy's windows rendered to PNG files (the probe renders them: dnSpy isn't on the screen)
		/// </summary>
		public string Screenshot(string name) {
			try {
				var path = Path.Combine(Samples.OutputRoot, "screenshots", $"{DateTime.Now:HHmmss}-{Sanitize(name)}");
				var files = Call("screenshot", new JsonObject { ["path"] = path })!.AsArray().Select(a => (string)a!);
				return string.Join(", ", files);
			}
			catch (Exception ex) {
				return "(no screenshot: " + ex.Message + ")";
			}
		}

		static string Sanitize(string name) => new string(name.Select(c => char.IsLetterOrDigit(c) ? c : '_').Take(60).ToArray());

		// ---- Input (through the probe: no focus needed)

		/// <summary>
		/// Presses keys in dnSpy's focused element: "F10", "Ctrl+Shift+E", "Shift+F5"
		/// </summary>
		public void Keys(string keys) {
			Call("key", new JsonObject { ["keys"] = keys });
			Idle();
		}

		// ---- Dialogs

		/// <summary>
		/// dnSpy's visible windows other than the main window: WPF type name and title
		/// </summary>
		public List<(string type, string title)> Dialogs() =>
			Call("windows")!.AsArray().Where(a => !(bool)a!["isMain"]!)
				.Select(a => ((string)a!["type"]!, (string)a["title"]!)).ToList();

		bool HasDialog(string typeName) => Dialogs().Any(a => a.type.EndsWith(typeName, StringComparison.Ordinal));

		/// <summary>
		/// Waits for a dnSpy window whose WPF type name ends with <paramref name="typeName"/>
		/// </summary>
		public void WaitForDialog(string typeName, TimeSpan? timeout = null) => WaitUntil(() => HasDialog(typeName), "dialog " + typeName, timeout);

		public void WaitForDialogClosed(string typeName, TimeSpan? timeout = null) => WaitUntil(() => !HasDialog(typeName), "dialog " + typeName + " to close", timeout);

		/// <summary>
		/// Clicks a button (by its name or text, without the access key's underscore)
		/// </summary>
		public void Click(string window, string button) {
			// (WPF enables a button later, when it asks its command again)
			WaitUntil(() => {
				try {
					Call("click", new JsonObject { ["window"] = window, ["button"] = button });
					return true;
				}
				catch (ProbeCallException ex) when (ex.Message.Contains("is disabled", StringComparison.Ordinal)) {
					return false;
				}
			}, $"button {button} to be enabled", TimeSpan.FromSeconds(15));
			Idle();
		}

		public void SetText(string window, string name, string text) {
			Call("setText", new JsonObject { ["window"] = window, ["name"] = name, ["text"] = text });
			Idle();
		}

		public void SetChecked(string window, string name, bool value) {
			Call("setChecked", new JsonObject { ["window"] = window, ["name"] = name, ["value"] = value });
			Idle();
		}

		/// <summary>
		/// Escape in the newest dialog: its Cancel button, or closes it
		/// </summary>
		public void CancelDialog() {
			Call("cancelDialog");
			Idle();
		}

		public bool? IsChecked(string window, string name) => (bool?)Call("isChecked", new JsonObject { ["window"] = window, ["name"] = name });

		/// <summary>
		/// Clicks a main menu item: Menu("File", "Export to Project...")
		/// </summary>
		public void Menu(params string[] path) {
			var array = new JsonArray(path.Select(a => (JsonNode)a).ToArray());
			for (int level = 0; level < path.Length; level++) {
				Call("menuStep", new JsonObject { ["path"] = array.DeepClone(), ["level"] = level });
				Idle();
			}
		}

		// ---- Errors

		/// <summary>
		/// dnSpy bugs that the tests hit but that aren't what they test. They're logged, not failures: an unobserved task's
		/// exception is raised by the finalizer, during whatever test is running when the GC runs.
		/// </summary>
		static readonly (string signature, string description)[] KnownIssues = Array.Empty<(string, string)>();

		/// <summary>
		/// The known issues seen since the last <see cref="TakeKnownIssues"/>
		/// </summary>
		readonly List<string> KnownIssuesSeen = new List<string>();

		public List<string> TakeKnownIssues() {
			var result = KnownIssuesSeen.ToList();
			KnownIssuesSeen.Clear();
			return result;
		}

		/// <summary>
		/// Exceptions that reached dnSpy's top level and dialogs that are still open (message boxes)
		/// </summary>
		public List<string> TakeProblems() {
			var problems = new List<string>();
			foreach (var error in Call("errors")!.AsArray().Select(a => (string)a!)) {
				var known = KnownIssues.FirstOrDefault(a => error.Contains(a.signature, StringComparison.Ordinal));
				if (known.signature is not null)
					KnownIssuesSeen.Add($"{known.description}: {error.Split('\n')[0]}");
				else
					problems.Add(error);
			}
			foreach (var (type, title) in Dialogs())
				problems.Add($"Dialog still open: {title} ({type})");
			return problems;
		}

		/// <summary>
		/// Back to an empty dnSpy between tests: closes the dialogs, stops debugging, removes the breakpoints, the tabs
		/// and the documents
		/// </summary>
		public void Reset() {
			if (process.HasExited)
				return;
			for (int i = 0; i < 10 && Dialogs().Count != 0; i++)
				CancelDialog();
			if ((bool)Call("dbg")!["isDebugging"]!) {
				Keys("Shift+F5");
				WaitUntil(() => !(bool)Call("dbg")!["isDebugging"]!, "debugging to stop");
			}
			Call("clearBreakpoints");
			var stop = Stopwatch.StartNew();
			while (stop.Elapsed < TimeSpan.FromSeconds(30) && (bool?)Call("tab")!["busy"] == true)
				Thread.Sleep(100);
			Call("closeAll");
			Idle();
			// (Not this test's)
			TakeProblems();
		}

		public bool HasExited => process.HasExited;

		public void Dispose() {
			try {
				Probe.Dispose();
			}
			catch {
			}
			try {
				if (!process.HasExited) {
					process.CloseMainWindow();
					// A "save changes?" prompt or a debugged program: don't keep anything
					if (!process.WaitForExit(15000))
						process.Kill(entireProcessTree: true);
				}
			}
			catch {
				try { process.Kill(entireProcessTree: true); } catch { }
			}
			RealKeyboard?.Dispose();
			process.Dispose();
		}
	}
}
