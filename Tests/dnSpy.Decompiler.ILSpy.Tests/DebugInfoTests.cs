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
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Checks the debug info the debugger consumes (statements, locals, async stepping) against the method's IL
	/// and against what the C# compiler wrote to its PDB for the same IL.
	/// </summary>
	public class DebugInfoTests {
		readonly ITestOutputHelper log;

		public DebugInfoTests(ITestOutputHelper log) => this.log = log;

		public static IEnumerable<object[]> Programs() {
			foreach (var program in new[] { nameof(TestPrograms.ControlFlow), nameof(TestPrograms.StateMachines), nameof(TestPrograms.Statements) }) {
				yield return new object[] { program, false };
				yield return new object[] { program, true };
			}
		}

		static string GetSource(string program) =>
			program switch {
				nameof(TestPrograms.ControlFlow) => TestPrograms.ControlFlow,
				nameof(TestPrograms.StateMachines) => TestPrograms.StateMachines,
				nameof(TestPrograms.Statements) => TestPrograms.Statements,
				_ => throw new ArgumentException(program),
			};

		sealed class EngineRun {
			public string Engine { get; }
			public List<DebugInfoReport> Reports { get; } = new List<DebugInfoReport>();
			public Dictionary<uint, MethodDebugInfo> InfosByMethod { get; } = new Dictionary<uint, MethodDebugInfo>();
			public List<string> MissingDebugInfo { get; } = new List<string>();
			public List<string> Exceptions { get; } = new List<string>();
			public EngineRun(string engine) => Engine = engine;

			public string Summary() {
				int Sum(Func<DebugInfoReport, int> f) => Reports.Sum(f);
				return $"{Engine,-9} errors={Sum(r => r.Errors.Count),3}  missing={MissingDebugInfo.Count,2}  exceptions={Exceptions.Count}  " +
					$"user-code covered={Percent(Sum(r => r.PdbUserCodeInstructionsCovered), Sum(r => r.PdbUserCodeInstructions)),-17}" +
					$"compiler breakpoint locations={Percent(Sum(r => r.PdbSequencePointsMatched), Sum(r => r.PdbSequencePoints)),-17}" +
					$"same code at shared breakpoints={Percent(Sum(r => r.SharedLocationsSameCode), Sum(r => r.SharedLocations)),-17}" +
					$"locals mapped={Percent(Sum(r => r.PdbLocalsMapped), Sum(r => r.PdbLocals)),-17}" +
					$"hoisted locals mapped/named={Sum(r => r.HoistedLocalsMapped)}/{Sum(r => r.HoistedLocalsNamed)} of {Sum(r => r.HoistedLocals)}  " +
					$"non-empty-stack starts={Sum(r => r.NonEmptyStackStarts)}";
			}
		}

		static EngineRun Run(string engine, ModuleDefMD module, Dictionary<int, PdbMethodInfo> pdb, string source, Func<TypeDef, (string text, IReadOnlyList<MethodDebugInfo> infos)> decompile) {
			var run = new EngineRun(engine);
			var methodsWithDebugInfo = new HashSet<uint>();
			foreach (var type in module.Types.Where(t => !t.IsGlobalModuleType)) {
				(string text, IReadOnlyList<MethodDebugInfo> infos) result;
				try {
					result = decompile(type);
				}
				catch (Exception ex) {
					run.Exceptions.Add($"{type.FullName}: {ex.GetType().Name}: {ex.Message}");
					continue;
				}
				foreach (var info in result.infos) {
					// An empty MethodDebugInfo doesn't let the debugger map anything
					if (info.Statements.Length != 0)
						methodsWithDebugInfo.Add(info.Method.MDToken.Raw);
					if (!run.InfosByMethod.TryGetValue(info.Method.MDToken.Raw, out var existing) || existing.Statements.Length < info.Statements.Length)
						run.InfosByMethod[info.Method.MDToken.Raw] = info;
					pdb.TryGetValue((int)info.Method.MDToken.Raw, out var pdbInfo);
					run.Reports.Add(DebugInfoValidator.Validate(info, result.text, pdbInfo, source));
				}
			}
			// Every method the compiler wrote sequence points for must get debug info, else the debugger can't
			// map it to decompiled code at all
			foreach (var p in pdb.Values) {
				if (p.SequencePointOffsets.Count != 0 && !methodsWithDebugInfo.Contains((uint)p.Token))
					run.MissingDebugInfo.Add(module.ResolveToken(p.Token)?.ToString() ?? $"0x{p.Token:X8}");
			}
			return run;
		}

		void Log(EngineRun run) {
			foreach (var r in run.Reports) {
				log.WriteLine($"  [{run.Engine}] {r}");
				foreach (var e in r.Errors)
					log.WriteLine($"  [{run.Engine}] ERROR {r.Method.Name}: {e}");
				var body = r.Method.Body;
				foreach (var offset in r.UncoveredUserCode) {
					var instr = body.Instructions.FirstOrDefault(i => i.Offset == offset);
					log.WriteLine($"  [{run.Engine}] UNCOVERED {r.Method.Name}: {instr}");
				}
				foreach (var w in r.WrongCode)
					log.WriteLine($"  [{run.Engine}] WRONG CODE {r.Method.Name}: {w}");
				foreach (var slot in r.UnmappedPdbLocals)
					log.WriteLine($"  [{run.Engine}] UNMAPPED LOCAL {r.Method.Name}: V_{slot} {body.Variables[slot].Type}");
			}
			foreach (var m in run.MissingDebugInfo)
				log.WriteLine($"  [{run.Engine}] No debug info for {m}");
			foreach (var e in run.Exceptions)
				log.WriteLine($"  [{run.Engine}] EXCEPTION {e}");
		}

		/// <summary>
		/// The offset of the first instruction of a call's arguments: going back from the call until the instructions
		/// have pushed all the values it pops
		/// </summary>
		static uint GetArgumentsStart(CilBody body, Instruction call) {
			int index = body.Instructions.IndexOf(call);
			call.CalculateStackUsage(out _, out int needed);
			for (int i = index - 1; i >= 0 && needed > 0; i--) {
				body.Instructions[i].CalculateStackUsage(out int pushes, out int pops);
				needed += pops - pushes;
				if (needed == 0)
					return body.Instructions[i].Offset;
			}
			return uint.MaxValue;
		}

		[Theory]
		[MemberData(nameof(Programs))]
		public void DebugInfo_IsConsistentWithIL_AndTheCompilersPdb(string program, bool optimize) {
			var compiled = TestCompiler.Compile(program + (optimize ? "Release" : "Debug"), GetSource(program), optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);

			// The decompiler dnSpy uses (dnSpy's output, token comments, indentation by the output)
			var language = LanguageRunner.Create();
			var source = GetSource(program);
			var upstream = Run("upstream", compiled.Load(), pdb, source, t => LanguageRunner.DecompileType(language, t));

			Log(upstream);
			log.WriteLine($"{program} ({(optimize ? "Release" : "Debug")})");
			log.WriteLine("  " + upstream.Summary());

			// Async builder field and SetResult offset, as the C# compiler generates them: the state machine's
			// '<>t__builder' field, and the first instruction of 'this.<>t__builder.SetResult(...)' in MoveNext, where
			// the call's arguments start ('ldarg.0'). (Async iterators have neither.)
			var asyncMismatches = new List<string>();
			int asyncMethods = 0;
			foreach (var kv in upstream.InfosByMethod) {
				if (kv.Value.AsyncInfo is not { } up)
					continue;
				var moveNext = kv.Value.Method;
				var builder = moveNext.DeclaringType.Fields.FirstOrDefault(a => a.Name == "<>t__builder");
				if (builder is null || builder.FieldType.TypeName.StartsWith("AsyncIteratorMethodBuilder", StringComparison.Ordinal))
					continue;
				asyncMethods++;
				var setResultCalls = moveNext.Body.Instructions.Where(a => (a.OpCode.Code == Code.Call || a.OpCode.Code == Code.Callvirt) &&
					a.Operand is IMethod m && m.Name == "SetResult" && m.DeclaringType?.FullName == builder.FieldType.FullName).ToList();
				if (up.BuilderField?.MDToken != builder.MDToken)
					asyncMismatches.Add($"{moveNext.FullName}: builder field {up.BuilderField}, expected {builder}");
				var expectedOffsets = setResultCalls.Select(a => GetArgumentsStart(moveNext.Body, a)).ToList();
				if (expectedOffsets.Count != 1 || up.SetResultOffset != expectedOffsets[0])
					asyncMismatches.Add($"{moveNext.FullName}: SetResult offset IL_{up.SetResultOffset:X4}, expected {string.Join(", ", expectedOffsets.Select(a => $"IL_{a:X4}"))}");
			}
			log.WriteLine($"  async methods checked: {asyncMethods}");
			foreach (var m in asyncMismatches)
				log.WriteLine("  ASYNC MISMATCH " + m);
			if (program == nameof(TestPrograms.StateMachines))
				Assert.True(asyncMethods >= 3, asyncMethods.ToString());

			Assert.Empty(upstream.Exceptions);
			Assert.Empty(upstream.MissingDebugInfo);
			Assert.Empty(upstream.Reports.SelectMany(r => r.Errors));
			Assert.Empty(asyncMismatches);
			// Regression guards (measured Oct 2026 after the statement shaping: coverage 84-91%, shared breakpoint
			// locations 59-90%; upstream's own sequence points alone gave 63-80% and 53-68%)
			Assert.True(Ratio(upstream.Reports.Sum(r => r.PdbUserCodeInstructionsCovered), upstream.Reports.Sum(r => r.PdbUserCodeInstructions)) >= 0.80, "User code IL coverage dropped below 80%");
			Assert.True(Ratio(upstream.Reports.Sum(r => r.PdbSequencePointsMatched), upstream.Reports.Sum(r => r.PdbSequencePoints)) >= 0.55, "Breakpoint locations shared with the compiler dropped below 55%");
			// Every local the compiler named (catch variables included) can be shown and evaluated, except the ones the
			// decompiled code doesn't have: 'var r = k switch { ... }; return r;' is decompiled as 'string result =
			// k switch { ... }; return result;' where 'result' is the switch's own temporary (upstream inlines 'r')
			var unmappedLocals = upstream.Reports.SelectMany(r => r.UnmappedPdbLocals.Select(slot => $"{r.Method.Name}:V_{slot}"))
				.Except(new[] { "SwitchDecl:V_0" }).ToList();
			Assert.Empty(unmappedLocals);
			int hoisted = upstream.Reports.Sum(r => r.HoistedLocals);
			if (hoisted != 0)
				Assert.True(Ratio(upstream.Reports.Sum(r => r.HoistedLocalsNamed), hoisted) >= 0.5, "Less than half of the hoisted locals are mapped with their name");
			// A statement at a compiler breakpoint location that's about different code means a breakpoint on it
			// would stop in the wrong place
			Assert.Empty(upstream.Reports.SelectMany(r => r.WrongCode));
		}

		/// <summary>
		/// Where the compiler's PDB has the closing brace of a method on its epilogue ('ret', or 'ldloc; ret' in debug
		/// code), so does the decompiled code: a step over the last statement stops there, and a breakpoint can be
		/// set on it. ILSpy 11.2 gives the brace the end of a branching last statement instead (the null path of
		/// 'Error?.Invoke(this, e);'), and the statement shaper must not give the epilogue to that statement.
		/// </summary>
		[Theory]
		[InlineData(nameof(TestPrograms.ControlFlow), false)]
		[InlineData(nameof(TestPrograms.ControlFlow), true)]
		[InlineData(nameof(TestPrograms.Statements), false)]
		[InlineData(nameof(TestPrograms.Statements), true)]
		public void MethodClosingBrace_IsOnTheCompilersEpilogue(string program, bool optimize) {
			var source = GetSource(program);
			var compiled = TestCompiler.Compile("Brace" + program + (optimize ? "Release" : "Debug"), source, optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var lines = source.Split('\n');
			var language = LanguageRunner.Create();
			var decompiled = new Dictionary<uint, (MethodDebugInfo info, string text)>();
			foreach (var type in compiled.Load().Types.Where(t => !t.IsGlobalModuleType)) {
				var (text, infos) = LanguageRunner.DecompileType(language, type);
				foreach (var info in infos) {
					if (!decompiled.TryGetValue(info.Method.MDToken.Raw, out var existing) || existing.info.Statements.Length < info.Statements.Length)
						decompiled[info.Method.MDToken.Raw] = (info, text);
				}
			}

			int braces = 0;
			var errors = new List<string>();
			foreach (var (info, text) in decompiled.Values) {
				var method = info.Method;
				if (method.Name == "MoveNext" || !pdb.TryGetValue((int)method.MDToken.Raw, out var p))
					continue;
				var instrs = method.Body.Instructions;
				uint codeSize = (uint)method.Body.GetCodeSize();
				var points = p.SequencePointOffsets.Concat(p.HiddenSequencePointOffsets).OrderBy(a => a).ToList();
				uint? brace = null;
				for (int i = 0; i < points.Count; i++) {
					if (!p.SourceLocations.TryGetValue(points[i], out var loc) || loc.startLine != loc.endLine ||
						lines[loc.startLine - 1].Substring(loc.startColumn - 1, loc.endColumn - loc.startColumn) != "}")
						continue;
					uint end = i + 1 < points.Count ? (uint)points[i + 1] : codeSize;
					if (instrs.Any(a => a.Offset >= points[i] && a.Offset < end && a.OpCode.Code == Code.Ret))
						brace = (uint)points[i];
				}
				if (brace is not uint offset)
					continue;
				braces++;
				var ours = info.Statements.Where(a => text.Substring(a.TextSpan.Start, a.TextSpan.Length) == "}" &&
					instrs.Any(i => i.Offset >= a.ILSpan.Start && i.Offset < a.ILSpan.End && i.OpCode.Code == Code.Ret)).ToList();
				// (Or a nop before it)
				if (ours.Count != 1 || ours[0].ILSpan.Start > offset || instrs.Any(a => a.Offset >= ours[0].ILSpan.Start && a.Offset < offset && a.OpCode.Code != Code.Nop))
					errors.Add($"{method.FullName}: the compiler's '}}' is at IL_{offset:X4}, the decompiled code's at {(ours.Count == 0 ? "none" : string.Join(", ", ours.Select(a => $"IL_{a.ILSpan.Start:X4}")))}");
			}
			foreach (var e in errors)
				log.WriteLine(e);
			log.WriteLine($"{program} ({(optimize ? "Release" : "Debug")}): {braces} closing braces on an epilogue in the compiler's PDB, {errors.Count} not in the decompiled code's");
			Assert.True(braces >= 2, braces.ToString());
			// Except where the decompiled code has no closing brace: the block bodied lambda 'x => { if (x > c)
			// return x; return c; }' is decompiled as 'x => (x > c) ? x : c'
			Assert.DoesNotContain(errors, a => !a.Contains("::<Lambdas>b__0(", StringComparison.Ordinal));
		}

		/// <summary>
		/// Where a call returns (the frame of the caller after a step out of the called method), the decompiled code has a
		/// statement wherever the compiler's PDB has one, so the frame shows its statement: e.g. the nop after
		/// 's.Raise();' in an async Main, which upstream gives to no statement.
		/// </summary>
		[Theory]
		[MemberData(nameof(Programs))]
		[InlineData("DebuggerPlayground", false)]
		[InlineData("DebuggerPlayground", true)]
		public void ReturnAddress_HasAStatement_WhereTheCompilersPdbHasOne(string program, bool optimize) {
			var source = program == "DebuggerPlayground" ?
				System.IO.File.ReadAllText(System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "DebuggerPlayground.cs")) :
				GetSource(program);
			var compiled = TestCompiler.Compile("Return" + program + (optimize ? "Release" : "Debug"), source, optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var language = LanguageRunner.Create();
			var decompiled = new Dictionary<uint, MethodDebugInfo>();
			foreach (var type in compiled.Load().Types.Where(t => !t.IsGlobalModuleType)) {
				foreach (var info in LanguageRunner.DecompileType(language, type).infos) {
					if (!decompiled.TryGetValue(info.Method.MDToken.Raw, out var existing) || existing.Statements.Length < info.Statements.Length)
						decompiled[info.Method.MDToken.Raw] = info;
				}
			}

			int returns = 0;
			var missing = new List<string>();
			foreach (var info in decompiled.Values) {
				var method = info.Method;
				if (!pdb.TryGetValue((int)method.MDToken.Raw, out var p))
					continue;
				var points = p.SequencePointOffsets.Select(o => (offset: o, hidden: false))
					.Concat(p.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(a => a.offset).ToList();
				var instrs = method.Body.Instructions;
				for (int i = 0; i + 1 < instrs.Count; i++) {
					if (instrs[i].OpCode.Code is not (Code.Call or Code.Callvirt))
						continue;
					uint offset = instrs[i + 1].Offset;
					var point = points.LastOrDefault(a => a.offset <= offset);
					if (point == default || point.hidden)
						continue;
					returns++;
					if (!info.Statements.Any(s => s.ILSpan.Start <= offset && offset < s.ILSpan.End))
						missing.Add($"{method.Name} IL_{offset:X4} after {instrs[i].Operand}");
				}
			}
			foreach (var m in missing)
				log.WriteLine("NO STATEMENT " + m);
			log.WriteLine($"{program} ({(optimize ? "Release" : "Debug")}): {returns} return addresses in a statement of the compiler's PDB, {missing.Count} without a statement in the decompiled code");
			Assert.True(returns >= 10, returns.ToString());
			// Except where the decompiled code has no statement for the call (see SteppingTests)
			var known = new (string method, string callee)[] {
				("MoveNext", "::<>m__Finally"),				// an iterator's calls of its finally block
				("Conditional", "::"),					// upstream merged an else-if into a conditional expression
				("Resources", "String::get_Length()"),		// the condition of a foreach over a string
			};
			Assert.DoesNotContain(missing, m => !known.Any(k => m.StartsWith(k.method + " ", StringComparison.Ordinal) && m.Contains(k.callee, StringComparison.Ordinal)));
		}

		/// <summary>
		/// Upstream (ILSpy 11.2) produces sequence points that start inside an instruction for this async method
		/// (IL_0040-IL_0043 and IL_0043-IL_0053, the latter attributed to the wrong statement). The adapter must
		/// never pass such statements to the debugger.
		/// </summary>
		[Fact]
		public void RealWorldAsync_InvalidUpstreamSequencePoints_AreRepaired() {
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0", "Newtonsoft.Json.dll");
			var module = ModuleDefMD.Load(path, TestCompiler.CreateModuleContext());
			var type = module.Find("Newtonsoft.Json.JsonWriter", false);

			// The adapter (reports what it repaired)
			var decompiler = TestDecompiler.Create();
			var result = decompiler.DecompileType(type);
			foreach (var repair in result.DebugInfoRepairs)
				log.WriteLine("REPAIRED " + repair);
			var moveNext = result.MethodDebugInfos.Single(i => i.Method.DeclaringType.Name == "<<InternalWriteEndAsync>g__AwaitRemaining|11_3>d");
			var report = DebugInfoValidator.Validate(moveNext, result.Text, null);
			foreach (var e in report.Errors)
				log.WriteLine("ERROR " + e);
			Assert.Empty(report.Errors);
			Assert.Empty(result.MethodDebugInfos.SelectMany(i => DebugInfoValidator.Validate(i, result.Text, null).Errors));

			// The decompiler dnSpy uses
			var (text, infos) = LanguageRunner.DecompileType(LanguageRunner.Create(), type);
			foreach (var info in infos) {
				foreach (var e in DebugInfoValidator.Validate(info, text, null).Errors)
					log.WriteLine($"ERROR {info.Method.FullName}: {e}");
			}
			Assert.Contains(infos, i => i.Method.DeclaringType.Name == "<<InternalWriteEndAsync>g__AwaitRemaining|11_3>d");
			Assert.Empty(infos.SelectMany(i => DebugInfoValidator.Validate(i, text, null).Errors));
		}

		/// <summary>
		/// Optimized real-world code (Newtonsoft.Json 13.0.4, netstandard2.0): upstream's statements had 93 breakpoint
		/// problems (a condition's tail or an expression's end attributed to another statement) in the whole
		/// assembly; the statement shaper leaves 8 (values kept on the stack across statements)
		/// </summary>
		[Fact]
		public void RealWorld_StatementsAreWellFormed() {
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0", "Newtonsoft.Json.dll");
			var module = ModuleDefMD.Load(path, TestCompiler.CreateModuleContext());
			var language = LanguageRunner.Create();
			var errors = new List<string>();
			int statements = 0;
			foreach (var name in new[] { "Newtonsoft.Json.JsonConvert", "Newtonsoft.Json.JsonSerializer", "Newtonsoft.Json.JsonReader", "Newtonsoft.Json.JsonWriter", "Newtonsoft.Json.Utilities.EnumUtils" }) {
				var (text, infos) = LanguageRunner.DecompileType(language, module.Find(name, false));
				foreach (var info in infos) {
					statements += info.Statements.Length;
					errors.AddRange(DebugInfoValidator.Validate(info, text, null).Errors.Select(e => $"{info.Method.FullName}: {e}"));
				}
			}
			foreach (var e in errors)
				log.WriteLine(e);
			log.WriteLine($"{errors.Count} errors in {statements} statements");
			Assert.True(statements > 1000);
			Assert.True(errors.Count <= 3, $"{errors.Count} errors");
		}

		static double Ratio(int a, int b) => b == 0 ? 1 : (double)a / b;

		static string Percent(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:0.0}% ({a}/{b})";
	}
}
