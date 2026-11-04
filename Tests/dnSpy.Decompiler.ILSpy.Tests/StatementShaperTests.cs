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
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The statement shaper fixes upstream's IL spans. It must leave well formed statements alone, so that it
	/// keeps working when upstream's sequence points get better: the C# compiler's own sequence points must pass
	/// through with the same breakpoint locations (dnSpy binds a statement's breakpoint to its lowest IL span) and
	/// IL. Allowed changes, because the compiler's statements aren't well formed for dnSpy there:
	/// <list type="bullet">
	/// <item>hiding a jump (br, leave, endfilter...) that other code also jumps to, e.g. the leave shared by a
	/// foreach's 'in' and a 'break'</item>
	/// <item>a statement that starts where the stack isn't empty (optimized code): no breakpoint can be bound there
	/// without a PDB, it can be given to the statement whose values it uses</item>
	/// <item>the method's epilogue (load the return value, ret) that the compiler hides in optimized code is the closing
	/// brace, like in debug code</item>
	/// </list>
	/// </summary>
	public class StatementShaperTests {
		readonly ITestOutputHelper log;

		public StatementShaperTests(ITestOutputHelper log) => this.log = log;

		public static IEnumerable<object[]> Programs() {
			foreach (var program in new[] { nameof(TestPrograms.ControlFlow), nameof(TestPrograms.StateMachines), nameof(TestPrograms.Statements) }) {
				foreach (var optimize in new[] { false, true }) {
					// Without a closing brace, and with the method's closing brace (production passes it)
					yield return new object[] { program, optimize, false };
					yield return new object[] { program, optimize, true };
				}
			}
		}

		static readonly Regex controlFlow = new Regex(@"^(if|while|for|foreach|switch|do|when|catch|using|lock|fixed|in|case|else|try|finally|await\s+foreach|await\s+using)\b");
		static readonly Regex jump = new Regex(@"^(break|continue|goto|yield\s+break)\b");
		static readonly Regex branches = new Regex(@"\?|&&|\|\||\bswitch\b|\bis\b|\bawait\b|\bthrow\b");

		[Theory]
		[MemberData(nameof(Programs))]
		public void CompilerSequencePoints_PassThroughUnchanged(string program, bool optimize, bool withClosingBrace) {
			var source = program switch {
				nameof(TestPrograms.ControlFlow) => TestPrograms.ControlFlow,
				nameof(TestPrograms.StateMachines) => TestPrograms.StateMachines,
				_ => TestPrograms.Statements,
			};
			var compiled = TestCompiler.Compile(program + (optimize ? "Release" : "Debug"), source, optimize);
			var pdb = PdbGroundTruth.Read(compiled.Pdb);
			var module = compiled.Load();
			var lines = source.Split('\n');
			int checkedMethods = 0;
			var problems = new List<string>();
			foreach (var info in pdb.Values.OrderBy(a => a.Token)) {
				if (info.SequencePointOffsets.Count == 0 || module.ResolveToken(info.Token) is not MethodDef method || method.Body is null)
					continue;
				uint codeSize = (uint)method.Body.GetCodeSize();
				var points = info.SequencePointOffsets.Select(o => (offset: o, hidden: false))
					.Concat(info.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(p => p.offset).ToList();
				var statements = new List<SourceStatement>();
				var kinds = new Dictionary<TextSpan, StatementKind>();
				var texts = new Dictionary<TextSpan, string>();
				TextSpan? closingBrace = null;
				for (int i = 0; i < points.Count; i++) {
					if (points[i].hidden)
						continue;
					uint start = (uint)points[i].offset;
					uint end = i + 1 < points.Count ? (uint)points[i + 1].offset : codeSize;
					if (end <= start)
						continue;
					// A fake text span per source location: same location = same statement, nested location = nested text
					var loc = info.SourceLocations[points[i].offset];
					var text = TextSpan.FromBounds(loc.startLine * 1000 + loc.startColumn, loc.endLine * 1000 + loc.endColumn);
					statements.Add(new SourceStatement(ILSpan.FromBounds(start, end), text));
					kinds[text] = GetKind(lines, loc);
					texts[text] = GetText(lines, loc);
					if (texts[text] == "}" && (closingBrace is null || text.Start > closingBrace.Value.Start))
						closingBrace = text;
				}
				// Production passes the closing brace of a block bodied method; with the compiler's sequence points it's
				// the last '}' (or one that doesn't have a sequence point)
				var brace = withClosingBrace ? closingBrace ?? TextSpan.FromBounds(999_000_000, 999_000_001) : (TextSpan?)null;
				if (brace is TextSpan b)
					kinds[b] = StatementKind.Brace;
				var visibleLocals = new HashSet<int>(method.Body.Variables.Select(a => a.Index));
				var context = new ShapeContext(span => kinds.TryGetValue(span, out var kind) ? kind : StatementKind.Other, visibleLocals, brace, isStateMachine: info.KickoffMethodToken != 0);
				var shaped = StatementShaper.Shape(statements, method, context);
				string Describe(TextSpan t) => texts.TryGetValue(t, out var s) ? s : "}(brace)";

				var expected = statements.Select(a => a.ToString()).OrderBy(a => a).ToArray();
				var actual = shaped.Select(a => a.ToString()).OrderBy(a => a).ToArray();
				if (!expected.SequenceEqual(actual)) {
					log.WriteLine($"{method.FullName}:");
					foreach (var s in statements.Except(shaped))
						log.WriteLine($"  - {s} `{Describe(s.TextSpan)}`");
					foreach (var s in shaped.Except(statements))
						log.WriteLine($"  + {s} `{Describe(s.TextSpan)}`");
				}
				var depths = StackDepthCalculator.Calculate(method);
				method.Body.UpdateInstructionOffsets();
				// Same breakpoint location (the lowest IL span's start), unless it can't be bound (the stack isn't empty)
				foreach (var g in statements.GroupBy(a => a.TextSpan)) {
					uint bp = g.Min(a => a.ILSpan.Start);
					var after = shaped.Where(a => a.TextSpan == g.Key).Select(a => a.ILSpan.Start).DefaultIfEmpty(uint.MaxValue).Min();
					if (after != bp && depths.GetValueOrDefault(bp) == 0)
						problems.Add($"{method.Name}: breakpoint of `{Describe(g.Key)}` moved IL_{bp:X4} -> {(after == uint.MaxValue ? "none" : $"IL_{after:X4}")}");
				}
				// No statement added
				foreach (var a in shaped) {
					if (!statements.Any(s => s.TextSpan == a.TextSpan) && a.TextSpan != brace)
						problems.Add($"{method.Name}: statement added: {a} `{Describe(a.TextSpan)}`");
				}
				// IL only moves to another statement or is hidden if it's a jump, or if it's a statement that starts where
				// the stack isn't empty
				foreach (var instr in method.Body.Instructions) {
					var before = statements.FirstOrDefault(s => s.ILSpan.Start <= instr.Offset && instr.Offset < s.ILSpan.End);
					var after = shaped.FirstOrDefault(s => s.ILSpan.Start <= instr.Offset && instr.Offset < s.ILSpan.End);
					if (before.TextSpan == after.TextSpan && before.ILSpan.IsEmpty == after.ILSpan.IsEmpty)
						continue;
					if (before.ILSpan.IsEmpty) {
						if (after.TextSpan == brace && (instr.OpCode.Code == Code.Ret || instr.IsLdloc() || instr.IsLdarg() || instr.OpCode.Code == Code.Nop))
							continue;
						problems.Add($"{method.Name}: IL_{instr.Offset:X4} {instr.OpCode} added to `{Describe(after.TextSpan)}`");
					}
					else if (IsJump(instr) || depths.GetValueOrDefault(before.ILSpan.Start) != 0)
						continue;
					else
						problems.Add($"{method.Name}: IL_{instr.Offset:X4} {instr.OpCode} moved from `{Describe(before.TextSpan)}` to {(after.ILSpan.IsEmpty ? "hidden" : $"`{Describe(after.TextSpan)}`")}");
				}
				checkedMethods++;
			}
			foreach (var p in problems)
				log.WriteLine(p);
			Assert.Empty(problems);
			Assert.True(checkedMethods > 5);
		}

		static SourceStatement S(uint start, uint end, int text) => new SourceStatement(ILSpan.FromBounds(start, end), TextSpan.FromBounds(text, text + 5));

		static MethodDef NewtonsoftMethod(string type, string name) {
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0", "Newtonsoft.Json.dll");
			var module = ModuleDefMD.Load(path, TestCompiler.CreateModuleContext());
			return module.Find(type, false).Methods.First(m => m.Name == name);
		}

		/// <summary>
		/// Merging a statement's pieces used a stale index (the standard CompareExchange loop of an event accessor with an
		/// expression's start attributed to the previous statement): ArgumentOutOfRangeException, no shaping
		/// </summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void MergingPieces_DoesNotFail(bool allVisible) {
			var method = NewtonsoftMethod("Newtonsoft.Json.JsonSerializer", "remove_Error");
			var input = new List<SourceStatement> { S(0x0, 0x17, 0), S(0x17, 0x1C, 10), S(0x1C, 0x29, 10) };
			var kinds = new Dictionary<int, StatementKind> { [0] = StatementKind.Simple, [10] = StatementKind.Other };
			var visible = allVisible ? new HashSet<int>(method.Body.Variables.Select(v => v.Index)) : new HashSet<int>();
			var context = new ShapeContext(t => kinds.TryGetValue(t.Start, out var k) ? k : StatementKind.Other, visible, TextSpan.FromBounds(99990, 99991), false);
			// (The module initializer makes the shaper throw instead of returning its input)
			var output = StatementShaper.Shape(input, method, context);
			Assert.Empty(CheckOutput(method, input, output, TextSpan.FromBounds(99990, 99991)));
		}

		/// <summary>
		/// Splitting stopped when the number of pieces didn't change ("split into two, drop one"): a piece was still
		/// entered in the middle
		/// </summary>
		[Fact]
		public void Splitting_ContinuesUntilEveryPieceIsEnteredAtItsStart() {
			var method = NewtonsoftMethod("Newtonsoft.Json.JsonWriter", "AutoCompleteAsync");
			var input = new List<SourceStatement> { S(0x0, 0x21, 0), S(0x21, 0x3B, 0), S(0x3B, 0x87, 20), S(0xA5, 0xB6, 20), S(0xB6, 0xF9, 30) };
			var kinds = new Dictionary<int, StatementKind> { [0] = StatementKind.Jump, [20] = StatementKind.Jump, [30] = StatementKind.Other };
			var context = new ShapeContext(t => kinds.TryGetValue(t.Start, out var k) ? k : StatementKind.Other, new HashSet<int>(), null, false);
			var output = StatementShaper.Shape(input, method, context);
			Assert.Empty(CheckOutput(method, input, output, null));
		}

		/// <summary>
		/// Random statements for every method of Newtonsoft.Json: the shaper never fails, and its output is well formed
		/// (instruction boundaries, no instruction in two statements, no new statements, every piece is only entered
		/// at its start from other code)
		/// </summary>
		[Fact]
		public void RandomStatements_OutputIsWellFormed() {
			var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0", "Newtonsoft.Json.dll");
			var module = ModuleDefMD.Load(path, TestCompiler.CreateModuleContext());
			var random = new Random(int.TryParse(Environment.GetEnvironmentVariable("SHAPER_FUZZ_SEED"), out int seed) ? seed : 12345);
			int runs = 0;
			var problems = new List<string>();
			foreach (var method in module.GetTypes().SelectMany(t => t.Methods)) {
				if (!method.HasBody || method.Body.Instructions.Count == 0)
					continue;
				var instrs = method.Body.Instructions;
				var offsets = new uint[instrs.Count + 1];
				uint offset = 0;
				for (int i = 0; i < instrs.Count; i++) {
					offsets[i] = offset;
					offset += (uint)instrs[i].GetSize();
				}
				offsets[instrs.Count] = offset;
				for (int iteration = 0; iteration < (int.TryParse(Environment.GetEnvironmentVariable("SHAPER_FUZZ_ITERATIONS"), out int n) ? n : 2); iteration++) {
					var input = RandomStatements(random, offsets, instrs.Count, out var kinds, out var brace);
					if (input.Count == 0)
						continue;
					var visible = new HashSet<int>(method.Body.Variables.Where(_ => random.Next(2) == 0).Select(v => v.Index));
					var context = new ShapeContext(t => kinds.TryGetValue(t, out var k) ? k : StatementKind.Other, visible, brace, random.Next(4) == 0);
					runs++;
					List<SourceStatement> output;
					try {
						output = StatementShaper.Shape(input, method, context);
					}
					catch (Exception ex) {
						problems.Add($"{method.FullName}: {ex.GetType().Name}: {ex.Message}; input: {string.Join(" ", input)}");
						continue;
					}
					foreach (var error in CheckOutput(method, input, output, brace).Take(2))
						problems.Add($"{method.FullName}: {error}; input: {string.Join(" ", input)}");
				}
			}
			foreach (var p in problems.Take(30))
				log.WriteLine(p);
			log.WriteLine($"{runs} runs, {problems.Count} problems");
			Assert.Empty(problems);
			Assert.True(runs > 5000);
		}

		static List<SourceStatement> RandomStatements(Random random, uint[] offsets, int count, out Dictionary<TextSpan, StatementKind> kinds, out TextSpan? brace) {
			kinds = new Dictionary<TextSpan, StatementKind>();
			var cuts = new SortedSet<int> { 0, count };
			int cutCount = random.Next(1, Math.Max(2, count / 2));
			for (int i = 0; i < cutCount; i++)
				cuts.Add(random.Next(0, count + 1));
			var list = cuts.ToList();
			var texts = new List<TextSpan>();
			int textCount = Math.Max(1, (list.Count - 1) * 2 / 3);
			for (int i = 0; i < textCount; i++) {
				var t = TextSpan.FromBounds(i * 10, i * 10 + 5);
				texts.Add(t);
				kinds[t] = (StatementKind)random.Next(0, 5);
			}
			brace = random.Next(3) == 0 ? null : texts[random.Next(texts.Count)];
			if (random.Next(2) == 0)
				brace = TextSpan.FromBounds(99990, 99991);
			if (brace is TextSpan b)
				kinds[b] = StatementKind.Brace;
			var result = new List<SourceStatement>();
			for (int i = 0; i + 1 < list.Count; i++) {
				// Some IL without a statement, mostly sequential texts, sometimes a statement with several pieces
				if (random.Next(5) == 0)
					continue;
				var text = random.Next(4) == 0 ? texts[random.Next(texts.Count)] : texts[Math.Min(texts.Count - 1, i * texts.Count / Math.Max(1, list.Count - 1))];
				result.Add(new SourceStatement(ILSpan.FromBounds(offsets[list[i]], offsets[list[i + 1]]), text));
			}
			return result;
		}

		static List<string> CheckOutput(MethodDef method, List<SourceStatement> input, List<SourceStatement> output, TextSpan? brace) {
			var errors = new List<string>();
			var instrs = method.Body.Instructions;
			var offsets = new uint[instrs.Count + 1];
			var indexOf = new Dictionary<Instruction, int>();
			uint offset = 0;
			for (int i = 0; i < instrs.Count; i++) {
				offsets[i] = offset;
				indexOf[instrs[i]] = i;
				offset += (uint)instrs[i].GetSize();
			}
			offsets[instrs.Count] = offset;
			var boundaries = new HashSet<uint>(offsets);
			var owner = new Dictionary<uint, TextSpan>();
			foreach (var s in output) {
				if (s.ILSpan.IsEmpty || !boundaries.Contains(s.ILSpan.Start) || !boundaries.Contains(s.ILSpan.End))
					errors.Add($"invalid span {s}");
				for (int i = 0; i < instrs.Count; i++) {
					if (offsets[i] >= s.ILSpan.Start && offsets[i] < s.ILSpan.End) {
						if (owner.ContainsKey(offsets[i]))
							errors.Add($"IL_{offsets[i]:X4} is in two statements");
						owner[offsets[i]] = s.TextSpan;
					}
				}
				if (!input.Any(a => a.TextSpan == s.TextSpan) && s.TextSpan != brace)
					errors.Add($"new statement {s}");
			}
			var entries = new List<(uint target, uint? source)>();
			foreach (var instr in instrs) {
				if (instr.Operand is Instruction target)
					entries.Add((offsets[indexOf[target]], offsets[indexOf[instr]]));
				else if (instr.Operand is IList<Instruction> targets) {
					foreach (var t in targets)
						entries.Add((offsets[indexOf[t]], offsets[indexOf[instr]]));
				}
			}
			foreach (var eh in method.Body.ExceptionHandlers) {
				if (eh.HandlerStart is not null)
					entries.Add((offsets[indexOf[eh.HandlerStart]], null));
				if (eh.FilterStart is not null)
					entries.Add((offsets[indexOf[eh.FilterStart]], null));
			}
			foreach (var s in output) {
				foreach (var (target, source) in entries) {
					if (target > s.ILSpan.Start && target < s.ILSpan.End && (source is not uint src || !owner.TryGetValue(src, out var t) || t != s.TextSpan))
						errors.Add($"{s} is entered at IL_{target:X4} from {(source is uint x ? $"IL_{x:X4}" : "an exception")}");
				}
			}
			return errors;
		}

		static bool IsJump(Instruction instr) =>
			instr.OpCode.Code is Code.Br or Code.Br_S or Code.Leave or Code.Leave_S or Code.Endfilter or Code.Endfinally or Code.Nop;

		static string GetText(string[] lines, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			var line = lines[loc.startLine - 1];
			return (loc.startLine == loc.endLine ? line.Substring(loc.startColumn - 1, loc.endColumn - loc.startColumn) : line.Substring(loc.startColumn - 1)).Trim();
		}

		static StatementKind GetKind(string[] lines, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			var line = lines[loc.startLine - 1];
			var text = GetText(lines, loc);
			if (text is "{" or "}")
				return StatementKind.Brace;
			if (jump.IsMatch(text))
				return StatementKind.Jump;
			if (controlFlow.IsMatch(text))
				return StatementKind.Other;
			// The condition and iterator of a for loop, and foreach's variable and collection, aren't statements
			var before = line.Substring(0, loc.startColumn - 1).TrimStart();
			if (before.StartsWith("for", StringComparison.Ordinal) || before.StartsWith("foreach", StringComparison.Ordinal) || before.StartsWith("await foreach", StringComparison.Ordinal))
				return StatementKind.Other;
			// Expression bodies of lambdas and members (=> x) and switch expression arms can be anything
			if (before.EndsWith("=>", StringComparison.Ordinal) || before.Contains("=> {", StringComparison.Ordinal))
				return StatementKind.Other;
			var full = loc.startLine == loc.endLine ? text : string.Join(" ", lines.Skip(loc.startLine - 1).Take(loc.endLine - loc.startLine + 1));
			return branches.IsMatch(full) ? StatementKind.SimpleBranching : StatementKind.Simple;
		}
	}
}
