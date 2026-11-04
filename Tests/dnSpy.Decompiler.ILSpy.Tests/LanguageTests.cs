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
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler.CSharp;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The decompiler dnSpy uses (<see cref="IDecompiler"/>): text, colors, references, braces, line separators and
	/// debug info as dnSpy's document viewer receives them
	/// </summary>
	public class LanguageTests {
		readonly ITestOutputHelper log;

		public LanguageTests(ITestOutputHelper log) => this.log = log;

		void Dump(CapturingDecompilerOutput output) {
			log.WriteLine(output.Text);
			foreach (var span in output.Spans.Where(a => a.Reference is not null))
				log.WriteLine(span.ToString());
		}

		static TypeDef Find(ModuleDef module, string fullName) => module.Find(fullName, false) ?? throw new InvalidOperationException(fullName);

		static IEnumerable<WrittenSpan> Spans(CapturingDecompilerOutput output, string text) => output.Spans.Where(a => a.Text == text);

		[Fact]
		public void AutoPropertyAccessors_HaveDebugInfo() {
			var module = TestCompiler.Compile("P1", "public class C { public int X { get; set; } public string Name { get; init; } = \"\"; public static int S { get; private set; } }").Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("C", false));
			foreach (var (name, accessor) in new[] { ("get_X", "get;"), ("set_X", "set;"), ("get_Name", "get;"), ("set_Name", "init;"), ("set_S", "private set;") }) {
				var info = Assert.Single(output.DebugInfos, i => i.Method.Name == name);
				var statement = Assert.Single(info.Statements);
				Assert.Equal(accessor, output.Text.Substring(statement.TextSpan.Start, statement.TextSpan.Length));
				Assert.Equal(0u, statement.ILSpan.Start);
				Assert.Equal((uint)info.Method.Body.GetCodeSize(), statement.ILSpan.End);
				Assert.Empty(DebugInfoValidator.Validate(info, output.Text, null).Errors);
			}
		}

		const string AsyncMainProgram = @"using System;
using System.Threading.Tasks;
namespace N;
public sealed class Program {
	private Program() { }
	[STAThread]
	public static async Task Main(string[] args) {
		await Task.Yield();
		Console.WriteLine(args.Length);
	}
}";

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void AsyncMain_CompilersEntryPointIsMappedToMain(bool optimize) {
			// The debugger breaks at the entry point, the compiler's <Main>, which upstream doesn't show
			var module = TestCompiler.Compile("AsyncMain", AsyncMainProgram, optimize, outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			var entryPoint = module.EntryPoint;
			Assert.Equal("<Main>", entryPoint.Name);
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "N.Program"));
			log.WriteLine(output.Text);
			Assert.DoesNotContain("<Main>", output.Text);

			var info = Assert.Single(output.DebugInfos, i => i.Method == entryPoint);
			var statement = Assert.Single(info.Statements);
			Assert.Equal("public static async Task Main(string[] args)", output.Text.Substring(statement.TextSpan.Start, statement.TextSpan.Length));
			Assert.Equal(0u, statement.ILSpan.Start);
			Assert.Equal((uint)entryPoint.Body.GetCodeSize(), statement.ILSpan.End);
			Assert.Equal("args", Assert.Single(info.Parameters!).Name);
			Assert.Empty(DebugInfoValidator.Validate(info, output.Text, null).Errors);

			// Main alone (dnSpy's option to not decompile the whole type)
			var mainOutput = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "N.Program").FindMethod("Main"));
			var mainStatement = Assert.Single(Assert.Single(mainOutput.DebugInfos, i => i.Method == entryPoint).Statements);
			Assert.Equal("public static async Task Main(string[] args)", mainOutput.Text.Substring(mainStatement.TextSpan.Start, mainStatement.TextSpan.Length));
		}

		[Fact]
		public void AsyncMain_WithoutParametersReturningInt_IsMappedToMain() {
			var module = TestCompiler.Compile("AsyncMainInt", "using System.Threading.Tasks; static class P { static async Task<int> Main() { await Task.Yield(); return 3; } }",
				outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "P"));
			var info = Assert.Single(output.DebugInfos, i => i.Method == module.EntryPoint);
			var statement = Assert.Single(info.Statements);
			Assert.Equal("private static async Task<int> Main()", output.Text.Substring(statement.TextSpan.Start, statement.TextSpan.Length));
			Assert.Empty(info.Parameters!);
		}

		[Fact]
		public void AsyncMain_ShownByUpstream_KeepsItsOwnDebugInfo() {
			// Without async/await decompilation upstream shows <Main> (like it would if it stopped hiding it): its own
			// debug info, nothing added
			var module = TestCompiler.Compile("AsyncMain", AsyncMainProgram, outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(s => s.Settings.AsyncAwait = false), Find(module, "N.Program"));
			log.WriteLine(output.Text);
			Assert.Contains("<Main>", output.Text);
			var info = Assert.Single(output.DebugInfos, i => i.Method == module.EntryPoint);
			Assert.All(info.Statements, s => Assert.DoesNotContain("async Task Main", output.Text.Substring(s.TextSpan.Start, s.TextSpan.Length)));
		}

		[Theory]
		[InlineData(false, true)]
		[InlineData(true, true)]
		[InlineData(false, false)]
		[InlineData(true, false)]
		public void AsyncMain_StepOverInCompilersEntryPoint_StepsIntoMain(bool optimize, bool parameters) {
			// The stepper's decision: stopped in <Main> before it calls Main (Break at Entry Point), the statement there is
			// Main's declaration and includes the call, so step over steps into Main instead of running the whole program.
			// Without parameters, the call is <Main>'s first instruction.
			var source = parameters ? AsyncMainProgram : AsyncMainProgram.Replace("Main(string[] args)", "Main()").Replace("args.Length", "0");
			var module = TestCompiler.Compile("AsyncMain", source, optimize, outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			var entryPoint = module.EntryPoint;
			Assert.True(Debugger.DotNet.Steppers.Engine.CompilerUtils.TryGetAsyncMainCallOffset(entryPoint, out var callOffset));
			Assert.Same(Find(module, "N.Program").FindMethod("Main"), entryPoint.Body.Instructions.Single(i => i.Offset == callOffset).Operand);

			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "N.Program"));
			var info = Debugger.DotNet.Code.DbgMethodDebugInfoUtils.ToDbgMethodDebugInfo(Assert.Single(output.DebugInfos, i => i.Method == entryPoint));
			bool StepsIntoMain(Contracts.Debugger.DotNet.Code.DbgMethodDebugInfo debugInfo, uint offset) =>
				Debugger.DotNet.Steppers.Engine.CompilerUtils.IsBeforeAsyncMainCall(debugInfo, offset);
			Assert.True(StepsIntoMain(info, 0));
			Assert.True(StepsIntoMain(info, Contracts.Debugger.DotNet.Code.DbgDotNetInstructionOffsetConstants.PROLOG));
			Assert.True(StepsIntoMain(info, callOffset));
			// Main returned: a normal step over
			Assert.False(StepsIntoMain(info, entryPoint.Body.Instructions.First(i => i.Offset > callOffset).Offset));
			Assert.False(StepsIntoMain(info, Contracts.Debugger.DotNet.Code.DbgDotNetInstructionOffsetConstants.EPILOG));

			// If upstream shows <Main>, its statement includes the call too
			var shown = LanguageRunner.Decompile(LanguageRunner.Create(s => s.Settings.AsyncAwait = false), Find(module, "N.Program"));
			Assert.True(StepsIntoMain(Debugger.DotNet.Code.DbgMethodDebugInfoUtils.ToDbgMethodDebugInfo(Assert.Single(shown.DebugInfos, i => i.Method == entryPoint)), 0));

			// IL: each instruction is a statement, a normal step over
			var il = LanguageRunner.Decompile(new Core.IL.ILDecompiler(DecompilerSettingsService.__Instance_DONT_USE.ILDecompilerSettings), entryPoint);
			var ilInfo = Debugger.DotNet.Code.DbgMethodDebugInfoUtils.ToDbgMethodDebugInfo(Assert.Single(il.DebugInfos, i => i.Method == entryPoint));
			Assert.False(StepsIntoMain(ilInfo, 0));
		}

		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void AsyncMain_EditMethodOnItsDeclaration_EditsMain(bool parameters) {
			// Edit Method (C#) in the code view uses the method of the statement at the caret: on Main's declaration that's
			// the compiler's entry point, which has no code of its own, so it edits Main
			var source = parameters ? AsyncMainProgram : AsyncMainProgram.Replace("Main(string[] args)", "Main()").Replace("args.Length", "0");
			var module = TestCompiler.Compile("AsyncMain", source, outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			var main = Find(module, "N.Program").FindMethod("Main");
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "N.Program"));
			int declaration = output.Text.IndexOf(parameters ? "public static async Task Main(string[] args)" : "public static async Task Main()", StringComparison.Ordinal);
			var atCaret = Assert.Single(output.DebugInfos, i => i.Statements.Any(s => s.TextSpan.Start <= declaration && declaration < s.TextSpan.End)).Method;
			Assert.Same(module.EntryPoint, atCaret);
			Assert.True(dnSpy.Decompiler.Utils.StateMachineHelpers.TryGetAsyncMainMethod(atCaret, out var edited));
			Assert.Same(main, edited);
			Assert.False(dnSpy.Decompiler.Utils.StateMachineHelpers.TryGetAsyncMainMethod(main, out _));
		}

		[Fact]
		public void SyncMain_GetsNoExtraDebugInfo() {
			var module = TestCompiler.Compile("SyncMain", "static class P { static void Main() { System.Console.WriteLine(1); } static void Other() { } }",
				outputKind: Microsoft.CodeAnalysis.OutputKind.ConsoleApplication).Load();
			Assert.False(Debugger.DotNet.Steppers.Engine.CompilerUtils.TryGetAsyncMainCallOffset(module.EntryPoint, out _));
			Assert.False(dnSpy.Decompiler.Utils.StateMachineHelpers.TryGetAsyncMainMethod(module.EntryPoint, out _));
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "P"));
			Assert.Equal(new[] { "Main", "Other" }, output.DebugInfos.Select(i => i.Method.Name.String).OrderBy(a => a, StringComparer.Ordinal));
		}

		[Fact]
		public void Type_IsColoredLikeDnSpysOtherDecompilers() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "Container`1"));
			Dump(output);

			Assert.Contains("public class Container<T> where T : class", output.Text);
			foreach (var keyword in new[] { "public", "class", "where", "return", "void", "this", "event", "get", "set", "struct", "using" })
				Assert.All(Spans(output, keyword), s => Assert.Equal(BoxedTextColor.Keyword, s.Color));
			Assert.All(Spans(output, "2"), s => Assert.Equal(BoxedTextColor.Number, s.Color));
			Assert.All(Spans(output, "System"), s => Assert.Equal(BoxedTextColor.Namespace, s.Color));
			Assert.All(Spans(output, "{").Concat(Spans(output, "(")).Concat(Spans(output, ";")), s => Assert.Equal(BoxedTextColor.Punctuation, s.Color));
			Assert.All(Spans(output, "=>").Concat(Spans(output, "*")), s => Assert.Equal(BoxedTextColor.Operator, s.Color));
			Assert.Equal(BoxedTextColor.InstanceMethod, Spans(output, "Convert").Single().Color);
			Assert.Equal(BoxedTextColor.InstanceProperty, Spans(output, "Value").Single().Color);
			Assert.Equal(BoxedTextColor.InstanceEvent, Spans(output, "Changed").First().Color);
			Assert.Equal(BoxedTextColor.InstanceField, Spans(output, "Item").Single().Color);
			Assert.Equal(BoxedTextColor.StaticField, Spans(output, "Empty").Single().Color);
			Assert.Equal(BoxedTextColor.Delegate, Spans(output, "Func").Single().Color);
			Assert.All(Spans(output, "T"), s => Assert.Equal(BoxedTextColor.TypeGenericParameter, s.Color));
			Assert.All(Spans(output, "U"), s => Assert.Equal(BoxedTextColor.MethodGenericParameter, s.Color));
			Assert.All(Spans(output, "converter"), s => Assert.Equal(BoxedTextColor.Parameter, s.Color));
			Assert.All(output.Spans.Where(s => s.Text.StartsWith("//", StringComparison.Ordinal)), s => Assert.Equal(BoxedTextColor.Comment, s.Color));
		}

		[Fact]
		public void References_AreDnlibObjects() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var container = Find(module, "Container`1");
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), container);

			// Nothing from the upstream type system leaks into dnSpy (it wouldn't know what to do with it)
			Assert.All(output.References, s => Assert.False(s.Reference!.GetType().Namespace!.StartsWith("ICSharpCode", StringComparison.Ordinal), s.ToString()));

			WrittenSpan Definition(string text) => Spans(output, text).Single(s => (s.Flags & DecompilerReferenceFlags.Definition) != 0);
			Assert.Same(container, Definition("Container").Reference);
			Assert.Same(container.FindMethod("Convert"), Definition("Convert").Reference);
			Assert.Same(container.FindProperty("Value"), Definition("Value").Reference);
			Assert.Same(container.FindEvent("Changed"), Definition("Changed").Reference);
			Assert.Same(container.NestedTypes.Single().FindField("Item"), Definition("Item").Reference);
			Assert.Same(container.GenericParameters[0], Definition("T").Reference);
			Assert.Same(container.FindMethod("Convert").GenericParameters[0], Definition("U").Reference);

			// References to other assemblies resolve to their definitions
			var empty = Assert.IsAssignableFrom<FieldDef>(Spans(output, "Empty").Single().Reference);
			Assert.Equal("System.EventArgs", empty.DeclaringType.FullName);

			// All uses of a variable share one reference object, so they're highlighted together
			var converter = Spans(output, "converter").ToArray();
			Assert.Equal(2, converter.Length);
			Assert.Same(converter[0].Reference, converter[1].Reference);
			Assert.IsType<Parameter>(converter[0].Reference);
			Assert.All(converter, s => Assert.True((s.Flags & DecompilerReferenceFlags.Local) != 0));
			// Indexers have no body in the declaration: their parameters are still references
			Assert.IsType<Parameter>(Spans(output, "index").First().Reference);

			// Delegate invocation on a generic instance: the instantiated member (for the tooltip)
			var invoke = Spans(output, "(").Select(s => s.Reference).OfType<MemberRef>().First();
			Assert.Equal("Invoke", invoke.Name);
			Assert.Equal("System.Func`2<T,U>", invoke.DeclaringType.FullName);
		}

		[Fact]
		public void BracePairs_AreAtTheBraces() {
			var module = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow).Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "ControlFlow"));
			var text = output.Text;
			var pairs = output.BracePairs.ToArray();
			Assert.NotEmpty(pairs);
			foreach (var pair in pairs) {
				var open = text.Substring(pair.Left.Start, pair.Left.Length);
				var close = text.Substring(pair.Right.Start, pair.Right.Length);
				var expected = pair.Flags.ToBraceKind() switch {
					CodeBracesRangeFlags.BraceKind_CurlyBraces => ("{", "}"),
					CodeBracesRangeFlags.BraceKind_Parentheses => ("(", ")"),
					CodeBracesRangeFlags.BraceKind_SquareBrackets => ("[", "]"),
					CodeBracesRangeFlags.BraceKind_AngleBrackets => ("<", ">"),
					CodeBracesRangeFlags.BraceKind_DoubleQuotes => ("\"", "\""),
					CodeBracesRangeFlags.BraceKind_SingleQuotes => ("'", "'"),
					_ => throw new InvalidOperationException(pair.Flags.ToString()),
				};
				Assert.Equal(expected, (open, close));
				Assert.True(pair.Left.End <= pair.Right.Start);
			}
			// Every curly brace that's punctuation (not in a string) is part of a pair
			var curly = output.Spans.Where(s => (s.Text == "{" || s.Text == "}") && Equals(s.Color, BoxedTextColor.Punctuation)).Select(s => s.Start).ToHashSet();
			var paired = pairs.Where(p => p.Flags.ToBraceKind() == CodeBracesRangeFlags.BraceKind_CurlyBraces).SelectMany(p => new[] { p.Left.Start, p.Right.Start }).ToHashSet();
			Assert.Equal(curly.OrderBy(a => a), paired.OrderBy(a => a));

			var blockKinds = pairs.Select(p => p.Flags).ToHashSet();
			foreach (var kind in new[] { CodeBracesRangeFlags.TypeBraces, CodeBracesRangeFlags.MethodBraces, CodeBracesRangeFlags.LoopBraces, CodeBracesRangeFlags.ConditionalBraces,
				CodeBracesRangeFlags.TryBraces, CodeBracesRangeFlags.CatchBraces, CodeBracesRangeFlags.FinallyBraces, CodeBracesRangeFlags.LockBraces, CodeBracesRangeFlags.SwitchBraces,
				CodeBracesRangeFlags.DoubleQuotes, CodeBracesRangeFlags.Parentheses }) {
				Assert.Contains(kind, blockKinds);
			}
		}

		[Fact]
		public void LineSeparators_AreAfterMembers() {
			var module = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow).Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "ControlFlow"));
			var separators = output.LineSeparators.ToArray();
			// After the using declarations and after each method except the last one
			Assert.Equal(1 + Find(module, "ControlFlow").Methods.Count(m => !m.IsConstructor) - 1, separators.Length);
			Assert.All(separators, p => Assert.Contains(output.Text[p], new[] { '}', ';' }));
		}

		[Fact]
		public void TokenComments_ReferenceTheMember() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var type = Find(module, "Constants");
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), type);
			var answer = type.FindMethod("Answer");
			var token = Spans(output, $"0x{answer.MDToken.Raw:X8}").Single();
			Assert.Equal(new TokenReference(answer), token.Reference);
			Assert.Contains($"// Token: 0x{answer.MDToken.Raw:X8} RID: {answer.Rid} RVA: 0x{(uint)answer.RVA:X8} File Offset: 0x", output.Text);
			Assert.IsType<AddressReference>(Spans(output, $"0x{(uint)answer.RVA:X8}").Single().Reference);

			var withoutTokens = LanguageRunner.Decompile(LanguageRunner.Create(s => s.ShowTokenAndRvaComments = false), type);
			Assert.DoesNotContain("Token:", withoutTokens.Text);
			Assert.Contains("return 42;", withoutTokens.Text);
		}

		[Fact]
		public void XmlDocumentation_IsShownAndColored() {
			const string source = """
				/// <summary>Adds <paramref name="a"/> and <paramref name="b"/></summary>
				/// <returns>The sum</returns>
				public static class Documented {
					/// <summary>Adds two numbers</summary>
					public static int Add(int a, int b) => a + b;
				}
				""";
			var dir = Path.Combine(Path.GetTempPath(), "dnSpy.Upstream.Tests", Guid.NewGuid().ToString("N"));
			try {
				var path = TestCompiler.CompileToDisk("Documented", source, dir);
				var module = ModuleDefMD.Load(path, TestCompiler.CreateModuleContext());
				var output = LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "Documented"));
				Dump(output);
				Assert.Contains("/// <summary>Adds two numbers</summary>", output.Text);
				Assert.Contains(output.Spans, s => s.Text == "///" && Equals(s.Color, BoxedTextColor.XmlDocCommentDelimiter));
				Assert.Contains(output.Spans, s => s.Text == "<summary>" && Equals(s.Color, BoxedTextColor.XmlDocCommentName));
				Assert.Contains(output.Spans, s => s.Text == "Adds two numbers" && Equals(s.Color, BoxedTextColor.XmlDocCommentText));

				var withoutDocs = LanguageRunner.Decompile(LanguageRunner.Create(s => s.Settings.ShowXmlDocumentation = false), Find(module, "Documented"));
				Assert.DoesNotContain("///", withoutDocs.Text);
			}
			finally {
				try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
			}
		}

		[Fact]
		public void SingleMembers_AreDecompiledLikeILSpy() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var container = Find(module, "Container`1");
			var language = LanguageRunner.Create(s => s.ShowTokenAndRvaComments = false);

			var method = LanguageRunner.Decompile(language, container.FindMethod("Convert")).Text;
			Assert.StartsWith("// Container<T>", method);
			Assert.Contains("public U Convert<U>(T value, Func<T, U> converter) where U : struct", method);
			Assert.DoesNotContain("Raise", method);

			var property = LanguageRunner.Decompile(language, container.FindProperty("Value")).Text;
			Assert.Contains("public T? Value { get; set; }", property);
			Assert.DoesNotContain("Convert", property);

			var ev = LanguageRunner.Decompile(language, container.FindEvent("Changed")).Text;
			Assert.Contains("public event EventHandler? Changed;", ev);

			var field = LanguageRunner.Decompile(language, container.NestedTypes[0].FindField("Item")).Text;
			Assert.Contains("public V? Item;", field);
		}

		[Fact]
		public void ShowMember_HidesWhatTheDecompilerShowsInline() {
			var module = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow).Load();
			var type = Find(module, "ControlFlow");
			var language = LanguageRunner.Create();
			var displayClass = type.NestedTypes.First(t => t.Name.StartsWith("<>c__DisplayClass", StringComparison.Ordinal));
			Assert.False(language.ShowMember(displayClass));
			Assert.True(language.ShowMember(type.FindMethod("Loops")));
			Assert.False(language.ShowMember(type.GetTypes().Prepend(type).SelectMany(t => t.Methods).Single(m => m.Name.String.Contains(">g__Local"))));

			var members = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var container = Find(members, "Container`1");
			Assert.False(language.ShowMember(container.Fields.Single(f => f.Name.String.Contains("k__BackingField"))));
			// Accessors are shown below their property
			Assert.True(language.ShowMember(container.FindMethod("get_Value")));

			var showAll = LanguageRunner.Create(s => s.ShowHiddenMembers = true);
			Assert.True(showAll.ShowMember(displayClass));
		}

		[Fact]
		public void EditedMethodBody_IsDecompiled() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var type = Find(module, "Constants");
			var method = type.FindMethod("Answer");
			var language = LanguageRunner.Create();
			Assert.Contains("return 42;", LanguageRunner.Decompile(language, type).Text);

			// Like dnSpy's IL editor: the body is replaced and the method is marked as modified
			foreach (var instr in method.Body.Instructions.Where(i => i.IsLdcI4())) {
				instr.OpCode = OpCodes.Ldc_I4_S;
				instr.Operand = (sbyte)43;
			}
			var ctx = LanguageRunner.CreateContext(m => m == method);
			var output = LanguageRunner.Decompile(language, type, ctx);
			Assert.Contains("return 43;", output.Text);
			Assert.Contains(output.DebugInfos, i => i.Method == method && i.Statements.Length != 0);
		}

		[Fact]
		public void RenamedType_IsDecompiledAfterTheModuleIsMarkedAsModified() {
			var module = TestCompiler.Compile("Members", TestPrograms.Members).Load();
			var type = Find(module, "Constants");
			var language = LanguageRunner.Create();
			Assert.Contains("class Constants", LanguageRunner.Decompile(language, type).Text);

			type.Name = "Renamed";
			// dnSpy's document tab service raises DocumentModified, the extension forwards it
			language.OnModulesModified(new[] { module });
			var output = LanguageRunner.Decompile(language, type);
			Assert.Contains("class Renamed", output.Text);
			Assert.Same(type, Spans(output, "Renamed").Single(s => (s.Flags & DecompilerReferenceFlags.Definition) != 0).Reference);
		}

		[Fact]
		public void SeveralNodesInOneOutput_HaveValidDebugInfo() {
			var compiled = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow);
			var module = compiled.Load();
			var language = LanguageRunner.Create();
			var output = new CapturingDecompilerOutput();
			// dnSpy writes a comment between nodes when several nodes are decompiled to one tab
			output.Write("// first", BoxedTextColor.Comment);
			output.WriteLine();
			LanguageRunner.Decompile(language, Find(module, "ControlFlow").FindMethod("Loops"), output: output);
			output.WriteLine();
			output.IncreaseIndent();
			LanguageRunner.Decompile(language, Find(module, "ControlFlow").FindMethod("Exceptions"), output: output);
			output.DecreaseIndent();

			var infos = output.DebugInfos.ToArray();
			Assert.Contains(infos, i => i.Method.Name == "Loops");
			Assert.Contains(infos, i => i.Method.Name == "Exceptions");
			foreach (var info in infos) {
				var report = DebugInfoValidator.Validate(info, output.Text, null);
				foreach (var e in report.Errors)
					log.WriteLine($"{info.Method}: {e}");
				Assert.Empty(report.Errors);
				foreach (var statement in info.Statements)
					Assert.InRange(statement.TextSpan.End, 0, output.Text.Length);
			}
			var exceptions = infos.First(i => i.Method.Name == "Exceptions");
			// The expression evaluator resolves type names with the using declarations ("using System;" for Console)
			Assert.Contains(exceptions.Scope.Imports, a => a.TargetKind == ImportInfoKind.Namespace && a.Target == "System");
			Assert.Contains(exceptions.Statements, s => output.Text.Substring(s.TextSpan.Start, s.TextSpan.Length).StartsWith("Console.WriteLine(\"done\")", StringComparison.Ordinal));
		}

		[Fact]
		public void MethodDecompiledAlone_ImportsTheNamespacesItsTypeUses() {
			// The debugger decompiles the method it stops in on its own: its code needs no usings, but Watch must resolve
			// 'Math', 'Path' and 'List<int>' like in the type's code (the expression evaluator uses the method's imports)
			const string source = @"using System;
using System.Collections.Generic;
using System.IO;
namespace N {
	public class C {
		object? value;
		public void Set(object? v) { value = v ?? ""default""; }
		public string File(string a) => Path.Combine(a, Math.Max(1, 2).ToString());
		public int Count(List<int> list) => list.Count;
		class Nested { public System.Text.StringBuilder? Sb; }
	}
}";
			var module = TestCompiler.Compile(nameof(MethodDecompiledAlone_ImportsTheNamespacesItsTypeUses), source).Load();
			var type = Find(module, "N.C");
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), type.FindMethod("Set"));
			log.WriteLine(output.Text);
			Assert.DoesNotContain("using ", output.Text);
			var imports = Assert.Single(output.DebugInfos, i => i.Method.Name == "Set").Scope.Imports.Select(a => a.Target).ToArray();
			foreach (var ns in new[] { "System", "System.IO", "System.Collections.Generic", "System.Text" })
				Assert.Contains(ns, imports);
			Assert.Equal(imports.Length, imports.Distinct().Count());

			// The type decompiled: its own usings first, nothing twice
			var typeOutput = LanguageRunner.Decompile(LanguageRunner.Create(), type);
			var typeImports = Assert.Single(typeOutput.DebugInfos, i => i.Method.Name == "Set").Scope.Imports.Select(a => a.Target).ToArray();
			Assert.Equal(typeImports.Length, typeImports.Distinct().Count());
			Assert.Equal(new[] { "System", "System.Collections.Generic", "System.IO" }, typeImports.Take(3));
		}

		/// <summary>
		/// dnSpy truncates very long names and escapes '@' and invalid characters, so the text is shorter or longer
		/// than the name; upstream computes some statement ends from the name's length
		/// </summary>
		[Fact]
		public void StatementsEndingAtRewrittenIdentifiers_HaveValidTextSpans() {
			// Upstream names a catch variable after its type: "a" + 900 x "A", truncated by dnSpy
			var longName = new string('A', 901);
			var source = $$"""
				using System;
				public class {{longName}} : Exception { }
				public class Event : Exception { }
				public class Names {
					public static string Catch(string s) {
						try {
							return s.Trim();
						}
						catch ({{longName}} e) {
							return e.Message;
						}
					}
					public static int Keyword(int @class) {
						try {
							return @class / 2;
						}
						catch (Event e) {
							return e.HResult;
						}
					}
				}
				""";
			var type = Find(TestCompiler.Compile("Names", source).Load(), "Names");
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), type);
			Assert.DoesNotContain(longName, output.Text);
			Assert.Contains(" a" + longName.Substring(0, 400), output.Text);
			Assert.Contains("@class", output.Text);
			var infos = output.DebugInfos.ToArray();
			Assert.Equal(2, infos.Length);
			foreach (var info in infos) {
				var errors = DebugInfoValidator.Validate(info, output.Text, null).Errors;
				foreach (var e in errors)
					log.WriteLine($"{info.Method.Name}: {e.Substring(0, Math.Min(300, e.Length))}");
				Assert.Empty(errors);
				// The catch statement ends at its ')'
				var catchStatement = info.Statements.Single(s => output.Text.Substring(s.TextSpan.Start, s.TextSpan.Length).StartsWith("catch", StringComparison.Ordinal));
				Assert.Equal(')', output.Text[catchStatement.TextSpan.End - 1]);
			}
		}

		[Fact]
		public void OutputWithoutCustomData_HasTheSameText() {
			var module = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow).Load();
			var type = Find(module, "ControlFlow");
			var language = LanguageRunner.Create();
			var full = LanguageRunner.Decompile(language, type);
			var plain = LanguageRunner.Decompile(language, type, output: new CapturingDecompilerOutput { UsesCustomData = false });
			Assert.Equal(full.Text, plain.Text);
			Assert.Empty(plain.CustomData);
			Assert.NotEmpty(full.DebugInfos);
		}

		[Fact]
		public void Cancellation_IsReported() {
			var module = TestCompiler.Compile("ControlFlow", TestPrograms.ControlFlow).Load();
			var ctx = LanguageRunner.CreateContext();
			using var cts = new CancellationTokenSource();
			cts.Cancel();
			ctx.CancellationToken = cts.Token;
			Assert.ThrowsAny<OperationCanceledException>(() => LanguageRunner.Decompile(LanguageRunner.Create(), Find(module, "ControlFlow"), ctx));
		}

		[Fact]
		public void DeeplyNestedCode_DoesNotOverflowTheStack() {
			// ldarg.0 x N, add x (N - 1): an expression nested N levels deep
			const int depth = 5000;
			var module = new ModuleDefUser("Deep.dll");
			module.RuntimeVersion = dnlib.DotNet.MD.MDHeaderRuntimeVersion.MS_CLR_40;
			var asm = new AssemblyDefUser("Deep");
			asm.Modules.Add(module);
			module.Context = TestCompiler.CreateModuleContext();
			var type = new TypeDefUser("", "Deep", module.CorLibTypes.Object.TypeDefOrRef) { Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed };
			module.Types.Add(type);
			var method = new MethodDefUser("Sum", MethodSig.CreateStatic(module.CorLibTypes.Int32, module.CorLibTypes.Int32), MethodAttributes.Public | MethodAttributes.Static);
			type.Methods.Add(method);
			var body = method.Body = new CilBody();
			for (int i = 0; i < depth; i++)
				body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
			for (int i = 1; i < depth; i++)
				body.Instructions.Add(OpCodes.Add.ToInstruction());
			body.Instructions.Add(OpCodes.Ret.ToInstruction());
			body.MaxStack = depth;

			var output = LanguageRunner.Decompile(LanguageRunner.Create(), type);
			Assert.Contains("public static int Sum(", output.Text);
		}

		[Fact]
		public void Settings_AreCreatedFromTheUpstreamSettings() {
			var all = UpstreamDecompilerSettings.AllSettings;
			foreach (var info in all)
				log.WriteLine($"{info.Group,-24} {info.Name,-50} {info.OptionName,-40} {info.Description}");
			Assert.True(all.Count > 100, $"Only {all.Count} settings");
			Assert.Equal(all.Count, all.Select(a => a.Guid).Distinct().Count());
			Assert.Equal(all.Count, all.Select(a => a.OptionName).Distinct().Count());
			// No resource keys or humanized names for the settings ILSpy has strings for
			Assert.DoesNotContain(all, a => a.Description.StartsWith("DecompilerSettings.", StringComparison.Ordinal));
			Assert.Equal("Decompile async methods", all.Single(a => a.Name == "AsyncAwait").Description);
			// dnSpy's previous C# decompiler's options keep their GUIDs (eg. the analyzer reads AnonymousMethods)
			Assert.Equal(DecompilerOptionConstants.AnonymousMethods_GUID, all.Single(a => a.Name == "AnonymousMethods").Guid);
			Assert.Equal(DecompilerOptionConstants.ShowXmlDocumentation_GUID, all.Single(a => a.Name == "ShowXmlDocumentation").Guid);
			Assert.Equal(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID, all.Single(a => a.Name == "ShowTokenAndRvaComments").Guid);
			// Settings that have no effect in dnSpy aren't shown
			Assert.DoesNotContain(all, a => a.Name == "ThrowOnAssemblyResolveErrors" || a.Name == "UseSdkStyleProjectFormat" || a.Name == "FoldBraces");

			var settings = new UpstreamDecompilerSettings();
			Assert.All(all, info => Assert.Equal(info.DefaultValue, info.GetValue(settings)));
			Assert.Equal(all.Count, settings.Options.Count());
			Assert.True(settings.GetBoolean(DecompilerOptionConstants.ShowTokenAndRvaComments_GUID));

			int version = settings.Version;
			int events = 0;
			settings.VersionChanged += (s, e) => events++;
			settings.Settings.AsyncAwait = false;
			settings.ShowTokenAndRvaComments = false;
			Assert.Equal(version + 2, settings.Version);
			Assert.Equal(2, events);

			var clone = (UpstreamDecompilerSettings)settings.Clone();
			Assert.Equal(settings, clone);
			Assert.False(clone.Settings.AsyncAwait);
			clone.Settings.AsyncAwait = true;
			Assert.NotEqual(settings, clone);
			Assert.False(settings.Settings.AsyncAwait);
		}

		[Fact]
		public void LanguageVersionPreset_DisablesNewerFeatures() {
			var settings = new UpstreamDecompilerSettings();
			settings.SetLanguageVersion(LanguageVersion.CSharp7_3);
			Assert.False(settings.Settings.RecordClasses);
			Assert.True(settings.Settings.NullPropagation);
			Assert.False(settings.Settings.SwitchExpressions);
			Assert.True(settings.Settings.AsyncAwait);
			Assert.Equal(LanguageVersion.CSharp7_3, settings.GetMinimumRequiredVersion());
			// The preset also enables features again
			settings.SetLanguageVersion(LanguageVersion.Latest);
			Assert.True(settings.Settings.RecordClasses);
			Assert.True(settings.Settings.SwitchExpressions);

			var module = TestCompiler.Compile("ModernCSharp", TestPrograms.ModernCSharp).Load();
			var csharp7 = LanguageRunner.Create(s => s.SetLanguageVersion(LanguageVersion.CSharp7_3));
			var person = LanguageRunner.Decompile(csharp7, Find(module, "Modern.Person")).Text;
			Assert.DoesNotContain("record", person);
			Assert.Contains("class Person", person);
		}
	}
}
