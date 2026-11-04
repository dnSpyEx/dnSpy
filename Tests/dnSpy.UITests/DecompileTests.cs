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
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// Many types shown in dnSpy's tabs in each language: none shows an error or a decompiler exception
	/// </summary>
	public sealed class DecompileTests : UITest {
		public DecompileTests(DnSpyFixture fixture, ITestOutputHelper log) : base(fixture, log) { }

		static int SampleCount => int.TryParse(Environment.GetEnvironmentVariable("DNSPY_UITEST_SAMPLES"), out int n) ? n : 30;

		/// <summary>
		/// What a failed decompilation shows (DecompileDocumentTabContent: "An error occurred" and the exception on the
		/// first lines; the decompiler's own errors), not words in the code (XML doc comments say "An error occurred")
		/// </summary>
		static readonly string[] errorMarkers = {
			"DecompilerException",
			"\n   at ICSharpCode.",
			"\n   at dnSpy.",
			"\n   at System.",
		};

		static string? FindError(string text) {
			if (text.StartsWith("An error occurred", StringComparison.Ordinal))
				return "An error occurred";
			return errorMarkers.FirstOrDefault(m => text.Replace("\r\n", "\n").Contains(m, StringComparison.Ordinal));
		}

		public static readonly TheoryData<string, string> Assemblies = new TheoryData<string, string> {
			{ "CoreLib", "C#" }, { "CoreLib", "IL with C#" }, { "CoreLib", "IL" },
			{ "Newtonsoft", "C#" }, { "Newtonsoft", "IL with C#" },
			{ "ConsoleSample", "C#" }, { "ConsoleSample", "IL with C#" },
			{ "NetFxSample", "C#" },
			{ "DebuggerPlayground", "C#" }, { "DebuggerPlayground", "IL with C#" },
		};

		static string GetFile(string name) => name switch {
			"CoreLib" => Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Private.CoreLib.dll"),
			"Newtonsoft" => Samples.Newtonsoft(),
			"ConsoleSample" => Samples.ConsoleSample(),
			"NetFxSample" => Samples.NetFxSample(),
			"DebuggerPlayground" => Samples.DebuggerPlayground("Release"),
			_ => throw new ArgumentException(name),
		};

		[Theory]
		[MemberData(nameof(Assemblies))]
		public void Types_DecompileWithoutErrors(string assembly, string language) {
			var file = GetFile(assembly);
			var module = ModuleDefMD.Load(file);
			var types = module.Types.Where(t => !t.IsGlobalModuleType).ToList();
			var random = new Random(assembly.Length * 31 + language.Length);
			if (types.Count > SampleCount)
				types = types.OrderBy(_ => random.Next()).Take(SampleCount).ToList();

			var failures = new List<string>();
			foreach (var type in types) {
				try {
					var tab = App.Show(file, "0x" + type.MDToken.Raw.ToString("X8"), language);
					var marker = FindError(tab.Text);
					if (marker is not null) {
						int index = Math.Max(0, tab.Text.Replace("\r\n", "\n").IndexOf(marker, StringComparison.Ordinal));
						failures.Add($"{type.FullName}: '{tab.Text.Substring(Math.Max(0, index - 200), Math.Min(600, tab.Text.Length - Math.Max(0, index - 200)))}'");
					}
					else if (!tab.Text.Contains(type.Name.String.Split('`')[0], StringComparison.Ordinal))
						failures.Add($"{type.FullName}: the name isn't in the code");
				}
				catch (Exception ex) {
					failures.Add($"{type.FullName}: {ex.Message}");
					App.Reset();
				}
			}
			Log.WriteLine($"{types.Count - failures.Count}/{types.Count} OK");
			foreach (var f in failures)
				Log.WriteLine(f);
			Assert.Empty(failures);
		}

		/// <summary>
		/// A tab closed while it decompiles (here: refreshed by a language switch, which keeps its UI state): when the
		/// decompilation finished, dnSpy restored the old UI state into the closed tab's disposed viewer
		/// (ObjectDisposedException in an unobserved task)
		/// </summary>
		[Fact]
		public void TabClosedWhileDecompiling_NoException() {
			var file = Path.Combine(RuntimeEnvironment.GetRuntimeDirectory(), "System.Private.CoreLib.dll");
			for (int i = 0; i < 6; i++) {
				App.Show(file, "System.String", i % 2 == 0 ? "C#" : "IL");
				// No wait: the tab is still decompiling when it's closed
				App.Call("setLanguage", new System.Text.Json.Nodes.JsonObject { ["name"] = i % 2 == 0 ? "IL" : "C#" });
				App.Call("closeAll");
				App.Idle();
			}
			// Let the decompilations finish, then the finalizer raises an unobserved exception
			System.Threading.Thread.Sleep(3000);
			App.Idle();
			App.Call("gc");
			App.Idle();
			var errors = App.Call("errors")!.AsArray().Select(a => (string)a!).ToList();
			Assert.True(errors.Count == 0, string.Join("\n\n", errors));
		}

		[Fact]
		public void GoToDefinition_InAnotherAssembly_AndBack() {
			var file = Samples.ConsoleSample();
			var start = App.Show(file, "ConsoleSample.Program::Main", "C#");
			App.CaretTo("new StringBuilder", delta: 4);
			App.Keys("F12");
			var tab = App.WaitForCode("StringBuilder");
			App.WaitUntil(() => App.Tab().Title.Contains("StringBuilder"), "the StringBuilder tab");
			tab = App.Tab();
			Log.WriteLine($"{tab.Title}: {tab.TrimmedLine}");
			Assert.Contains("StringBuilder", tab.LineText);

			App.Keys("Alt+Left");
			App.WaitUntil(() => App.Tab().Title == start.Title && App.Tab().Text == start.Text, "back to Main");
		}

		[Fact]
		public void GoToDefinition_SameAssembly() {
			var file = Samples.ConsoleSample();
			App.Show(file, "ConsoleSample.Program::Main", "C#");
			App.CaretTo("Describe(", delta: 1);
			App.Keys("F12");
			App.WaitUntil(() => App.Tab().LineText.Contains("Describe(object o)"), "Describe's definition");
		}

		[Fact]
		public void LanguageSwitch_RefreshesTheTab() {
			var file = Samples.DebuggerPlayground("Release");
			var cs = App.Show(file, "DebuggerPlayground.Scenarios::Switch", "C#");
			App.SetLanguage("IL");
			var il = App.WaitForCode("IL");
			Assert.Contains("IL_0000", il.Text);
			Assert.DoesNotContain("return 10;", il.Text);
			App.SetLanguage("IL with C#");
			var ilcs = App.WaitForCode("IL with C#");
			Assert.Contains("IL_0000", ilcs.Text);
			Assert.Contains("return 10;", ilcs.Text);
			App.SetLanguage("C#");
			Assert.Equal(cs.Text, App.WaitForCode("C#").Text);
		}
	}
}
