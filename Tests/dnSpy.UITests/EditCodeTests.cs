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
using dnlib.DotNet;
using dnSpy.UITests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.UITests {
	/// <summary>
	/// Edit Method (C#) in dnSpy's dialog (Ctrl+Shift+E, Compile), with the references dnSpy finds on this PC: on
	/// .NET 10, .NET Framework 4.8 and netstandard 2.0 assemblies. Then saved and run.
	/// </summary>
	public sealed class EditCodeTests : UITest {
		public EditCodeTests(DnSpyFixture fixture, ITestOutputHelper log) : base(fixture, log) { }

		/// <summary>
		/// How many methods of a big assembly (Newtonsoft.Json) the stress tests edit (DNSPY_UITEST_SAMPLES)
		/// </summary>
		static int SampleCount => int.TryParse(Environment.GetEnvironmentVariable("DNSPY_UITEST_SAMPLES"), out int n) ? n : 30;

		/// <summary>
		/// Methods a user can edit: in the non-generated types, with a body, not the compiler's helpers
		/// </summary>
		static List<MethodDef> EditableMethods(string file) {
			var module = ModuleDefMD.Load(file);
			return module.GetTypes()
				.Where(t => !t.IsGlobalModuleType && !IsGenerated(t))
				.SelectMany(t => t.Methods)
				.Where(m => m.HasBody && !m.Name.String.StartsWith("<", StringComparison.Ordinal) && !m.IsAbstract &&
					!m.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
				.ToList();
		}

		static bool IsGenerated(TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (t.Name.String.StartsWith("<", StringComparison.Ordinal) || t.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
					return true;
			}
			return false;
		}

		static bool IsSelectTokensStringBool(MethodDef m) =>
			m.MethodSig.Params.Count == 2 && m.MethodSig.Params[1].ElementType == ElementType.Boolean;

		static string Token(IMDTokenProvider member) => "0x" + member.MDToken.Raw.ToString("X8");

		/// <summary>
		/// Edit Method without changes on each method, Compile: collects the ones that don't compile and import
		/// </summary>
		List<string> EditWithoutChanges(string file, IEnumerable<MethodDef> methods) {
			var failures = new List<string>();
			int count = 0;
			foreach (var method in methods) {
				count++;
				try {
					var tab = App.Show(file, Token(method));
					var state = App.TryOpenEditMethod(TimeSpan.FromSeconds(10));
					if (state is null) {
						// A constructor the decompiler doesn't show (an empty default one, a primary constructor) has no
						// declaration in the code view, so there's no method at the caret (the tree view's context menu
						// edits it)
						if (!tab.Text.Contains("Token: " + Token(method), StringComparison.Ordinal)) {
							Log.WriteLine($"skip {method.FullName} (not declared in the code view)");
							count--;
							continue;
						}
						throw new InvalidOperationException("Ctrl+Shift+E didn't open Edit Method");
					}
					var errors = App.Compile();
					if (errors.Count != 0) {
						failures.Add($"{method.FullName}: {errors.Count} errors: {string.Join(" | ", errors.Take(3))}");
						App.CancelEditCode();
					}
					var problems = App.TakeProblems();
					if (problems.Count != 0)
						failures.Add($"{method.FullName}: {string.Join(" | ", problems)}");
					Log.WriteLine($"{(errors.Count == 0 ? "ok  " : "FAIL")} {method.FullName}");
				}
				catch (Exception ex) {
					failures.Add($"{method.FullName}: {ex.Message}");
					Log.WriteLine($"FAIL {method.FullName}: {ex.Message}");
					App.Reset();
				}
			}
			Log.WriteLine($"{count - failures.Count}/{count} compiled");
			return failures;
		}

		[Fact]
		public void EditMethod_NoChanges_DotNet10_AllMethods() {
			var file = Samples.CopyOf(Samples.ConsoleSample());
			Assert.Empty(EditWithoutChanges(file, EditableMethods(file)));
		}

		[Fact]
		public void EditMethod_NoChanges_DebuggerPlayground_AllMethods() {
			var file = Samples.CopyOf(Samples.DebuggerPlayground("Release"));
			Assert.Empty(EditWithoutChanges(file, EditableMethods(file)));
		}

		/// <summary>
		/// .NET Framework 4.8: mscorlib, no NullableAttribute in the references
		/// </summary>
		[Fact]
		public void EditMethod_NoChanges_NetFramework_AllMethods() {
			var file = Samples.CopyOf(Samples.NetFxSample());
			Assert.Empty(EditWithoutChanges(file, EditableMethods(file)));
		}

		/// <summary>
		/// A nullable-enabled netstandard 2.0 library: the references (netstandard 2.0's reference assembly) don't have
		/// the attributes the compiler embedded in it (they were CS0518 errors in every method with annotations)
		/// </summary>
		[Fact]
		public void EditMethod_NoChanges_NetStandardNullableLibrary_Sample() {
			var file = Samples.Newtonsoft();
			var all = EditableMethods(file);
			var random = new Random(1234);
			var methods = all.Where(m => m.DeclaringType.FullName == "Newtonsoft.Json.Linq.JProperty/JPropertyList" && m.Name == "get_Count")
				.Concat(all.Where(m => m.DeclaringType.FullName == "Newtonsoft.Json.Linq.JToken" && m.Name == "SelectTokens" && IsSelectTokensStringBool(m)))
				.Concat(all.OrderBy(_ => random.Next()).Take(SampleCount))
				.Distinct().ToList();
			Assert.Empty(EditWithoutChanges(file, methods));
		}

		[Fact]
		public void EditMethod_NetStandardNullableLibrary_SaveAndReopen_KeepsTheAnnotations() {
			var file = Samples.Newtonsoft();
			var selectTokens = EditableMethods(file).Single(m => m.DeclaringType.FullName == "Newtonsoft.Json.Linq.JToken" && m.Name == "SelectTokens" && IsSelectTokensStringBool(m));
			App.Show(file, Token(selectTokens));
			var state = App.OpenEditMethod();
			Assert.StartsWith("#nullable enable annotations", state.Documents["main.cs"].TrimStart());
			Assert.Empty(App.Compile());

			var saved = Path.Combine(Path.GetDirectoryName(file)!, "saved", "Newtonsoft.Json.dll");
			Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
			App.SaveModuleAs(saved);

			App.Call("closeAll");
			var tab = App.Show(saved, "Newtonsoft.Json.Linq.JToken");
			Assert.Contains("private JContainer? _parent;", tab.Text);
			Assert.Contains("private JToken? _previous;", tab.Text);
			Assert.Contains("public IEnumerable<JToken> SelectTokens(string path, bool errorWhenNoMatch)", tab.Text);
		}

		/// <summary>
		/// A .NET 9 library of a .NET 10 app: its reference NewLib is the app's .NET 10 build, so it's compiled against
		/// .NET 10 (with .NET 9's reference assemblies: CS1705, NewLib uses System.Runtime 10). Saved and run.
		/// </summary>
		[Fact]
		public void EditMethod_NetLibraryOfANewerApp_CompilesWithTheNewerFramework() =>
			EditSaveAndRunMixedFrameworks("OldLib.dll", "OldLib.Caller::Greet", "\"!\"", "\"?\"", "Hello world?\na, b.");

		/// <summary>
		/// A .NET Standard 2.0 library that uses System.Collections.Immutable, which isn't part of .NET Standard: it's
		/// .NET 10's, so it's compiled against .NET 10 (with .NET Standard's reference assemblies there were two core
		/// libraries: CS0518 System.Void isn't defined, CS0433). Saved and run.
		/// </summary>
		[Fact]
		public void EditMethod_NetStandardLibraryWithANetPackage_CompilesWithTheNetFramework() =>
			EditSaveAndRunMixedFrameworks("StandardLib.dll", "StandardLib.Names::Join", "\".\"", "\"!\"", "Hello world!\na, b!");

		void EditSaveAndRunMixedFrameworks(string fileName, string method, string find, string replace, string expected) {
			var file = Samples.CopyOf(Samples.MixedFrameworks(fileName));
			App.Show(file, method);
			var state = App.OpenEditMethod();
			Assert.Contains(find, state.Documents["main.cs"]);
			App.ReplaceInEditCode(find, replace);
			Assert.Empty(App.Compile());

			var dir = Path.GetDirectoryName(file)!;
			var savedDir = Path.Combine(dir, "saved");
			Directory.CreateDirectory(savedDir);
			foreach (var f in Directory.GetFiles(dir))
				File.Copy(f, Path.Combine(savedDir, Path.GetFileName(f)));
			var saved = Path.Combine(savedDir, Path.GetFileName(file));
			File.Delete(saved);
			App.SaveModuleAs(saved);
			Assert.Equal("exit 0\n" + expected, Samples.RunProgram(Path.Combine(savedDir, "App.dll")).Trim().Replace("\r\n", "\n"));
		}

		/// <summary>
		/// Edit Method on an async Main's declaration (the compiler's &lt;Main&gt; is shown as Main's declaration) edits Main
		/// </summary>
		[Fact]
		public void EditMethod_AsyncMainDeclaration_EditsMain() {
			var file = Samples.CopyOf(Samples.DebuggerPlayground("Release"));
			App.Show(file, "DebuggerPlayground.Program::Main");
			App.CaretTo("Main()");
			var state = App.OpenEditMethod();
			var main = state.Documents["main.cs"];
			Log.WriteLine(main);
			Assert.Contains("public static async Task Main()", main);
			Assert.DoesNotContain("<Main>", main);
			Assert.Empty(App.Compile());
		}

		/// <summary>
		/// Two edits of a method with collection expressions: the first imports the compiler's collection types again
		/// (renamed with a __N suffix), the decompiled code must still use collection expressions
		/// </summary>
		[Fact]
		public void EditMethod_Twice_CollectionExpressionsStay() {
			var file = Samples.CopyOf(Samples.ConsoleSample());
			for (int i = 1; i <= 3; i++) {
				var tab = App.Show(file, "ConsoleSample.Program::Main");
				Assert.Contains("Names([\"y\"])", tab.Text);
				Assert.DoesNotContain("<>z__", tab.Text);
				App.CaretTo("Main()");
				App.OpenEditMethod();
				var errors = App.Compile();
				Assert.True(errors.Count == 0, $"Edit {i}: " + string.Join(" | ", errors));
			}
			var after = App.Show(file, "ConsoleSample.Program::Main");
			Assert.Contains("Names([\"y\"])", after.Text);
			Assert.DoesNotContain("<>z__", after.Text);
		}

		/// <summary>
		/// A real change: compiled, saved and run, the program prints the new line and everything else the same
		/// </summary>
		[Fact]
		public void EditMethod_AddedStatement_SaveAndRun() {
			var file = Samples.CopyOf(Samples.ConsoleSample());
			var original = Samples.RunProgram(file);
			Log.WriteLine(original);

			App.Show(file, "ConsoleSample.Program::Sum");
			var state = App.OpenEditMethod();
			var main = state.Documents["main.cs"];
			int signature = main.IndexOf("Sum(params ReadOnlySpan<int> values)", StringComparison.Ordinal);
			Assert.True(signature >= 0, main);
			int brace = main.IndexOf('{', signature);
			var find = main.Substring(signature, brace - signature + 1);
			App.ReplaceInEditCode(find, find + "\nConsole.WriteLine(\"UITEST-MARKER\");");
			Assert.Empty(App.Compile());

			var saved = Path.Combine(Path.GetDirectoryName(file)!, "saved", Path.GetFileName(file));
			Directory.CreateDirectory(Path.GetDirectoryName(saved)!);
			foreach (var f in Directory.GetFiles(Path.GetDirectoryName(file)!))
				File.Copy(f, Path.Combine(Path.GetDirectoryName(saved)!, Path.GetFileName(f)), overwrite: true);
			File.Delete(saved);
			App.SaveModuleAs(saved);

			var edited = Samples.RunProgram(saved);
			Log.WriteLine(edited);
			Assert.Contains("UITEST-MARKER", edited);
			Assert.Equal(original, string.Join("\n", edited.Split('\n').Where(a => a.TrimEnd('\r') != "UITEST-MARKER")));
		}

		/// <summary>
		/// Code that doesn't compile: the errors are shown, nothing is imported, Cancel closes the dialog
		/// </summary>
		[Fact]
		public void EditMethod_CompilerError_IsShown_AndCancelKeepsTheMethod() {
			var file = Samples.CopyOf(Samples.ConsoleSample());
			var before = App.Show(file, "ConsoleSample.Program::Sum").Text;
			var state = App.OpenEditMethod();
			var main = state.Documents["main.cs"];
			int signature = main.IndexOf("Sum(params ReadOnlySpan<int> values)", StringComparison.Ordinal);
			int brace = main.IndexOf('{', signature);
			var find = main.Substring(signature, brace - signature + 1);
			App.ReplaceInEditCode(find, find + "\nreturn undefinedVariable;");
			var errors = App.Compile();
			Log.WriteLine(string.Join("\n", errors));
			Assert.Contains(errors, a => a.Code == "CS0103");
			App.CancelEditCode();
			Assert.Equal(before, App.Show(file, "ConsoleSample.Program::Sum").Text);
		}
	}
}
