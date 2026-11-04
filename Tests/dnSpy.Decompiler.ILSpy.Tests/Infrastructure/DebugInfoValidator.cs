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
using System.Linq;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// Result of checking one method's debug info
	/// </summary>
	sealed class DebugInfoReport {
		public MethodDef Method { get; }

		/// <summary>
		/// Hard errors: debug info the debugger can't use correctly
		/// </summary>
		public List<string> Errors { get; } = new List<string>();

		/// <summary>
		/// Statements starting where the IL evaluation stack isn't empty. Legal, but a breakpoint there stops in the
		/// middle of an expression.
		/// </summary>
		public int NonEmptyStackStarts { get; set; }

		public int Instructions { get; set; }
		public int CoveredInstructions { get; set; }

		/// <summary>
		/// Non-hidden sequence points in the compiler's PDB, and how many of them are also the start of a decompiled statement
		/// </summary>
		public int PdbSequencePoints { get; set; }
		public int PdbSequencePointsMatched { get; set; }

		/// <summary>
		/// Instructions in the compiler's non-hidden sequence point regions (user code), and how many of them are
		/// covered by a decompiled statement. Uncovered user code means stepping lands where there's no source.
		/// </summary>
		public int PdbUserCodeInstructions { get; set; }
		public int PdbUserCodeInstructionsCovered { get; set; }
		public List<uint> UncoveredUserCode { get; } = new List<uint>();
		public List<int> UnmappedPdbLocals { get; } = new List<int>();

		/// <summary>
		/// Decompiled statements that start at a compiler breakpoint location, and how many of them are about the
		/// same code as the original source statement there (shared identifiers). A statement that doesn't is mapped
		/// to the wrong IL: a breakpoint on it would stop somewhere else.
		/// </summary>
		public int SharedLocations { get; set; }
		public int SharedLocationsSameCode { get; set; }
		public List<string> WrongCode { get; } = new List<string>();

		/// <summary>
		/// Hoisted user locals (state machine fields &lt;name&gt;5__N) and how many of them the debug info maps,
		/// with the original name
		/// </summary>
		public int HoistedLocals { get; set; }
		public int HoistedLocalsMapped { get; set; }
		public int HoistedLocalsNamed { get; set; }

		public int PdbLocals { get; set; }
		public int PdbLocalsMapped { get; set; }

		public DebugInfoReport(MethodDef method) => Method = method;

		public override string ToString() =>
			$"{Method.FullName}: errors={Errors.Count}, coverage={CoveredInstructions}/{Instructions}, " +
			$"pdbSPs={PdbSequencePointsMatched}/{PdbSequencePoints}, hoisted={HoistedLocalsMapped}/{HoistedLocals} (named {HoistedLocalsNamed}), sameCode={SharedLocationsSameCode}/{SharedLocations}, userCodeCoverage={PdbUserCodeInstructionsCovered}/{PdbUserCodeInstructions}, pdbLocals={PdbLocalsMapped}/{PdbLocals}, nonEmptyStackStarts={NonEmptyStackStarts}";
	}

	/// <summary>
	/// Checks the debug info the debugger consumes (<see cref="MethodDebugInfo"/>) against the method's IL and,
	/// when available, against the compiler's PDB.
	/// </summary>
	static class DebugInfoValidator {
		public static DebugInfoReport Validate(MethodDebugInfo info, string text, PdbMethodInfo? pdb, string? originalSource = null) {
			var report = new DebugInfoReport(info.Method);
			var body = info.Method.Body;
			if (body is null) {
				report.Errors.Add("Method has no body");
				return report;
			}
			body.UpdateInstructionOffsets();
			var offsets = new HashSet<uint>(body.Instructions.Select(i => i.Offset));
			uint codeSize = (uint)body.GetCodeSize();
			var stackDepths = StackDepthCalculator.Calculate(info.Method);

			var entries = ControlFlowEntries.Calculate(body);
			var ilOwner = new Dictionary<uint, TextSpan>();
			// (Linear in the number of statements and instructions: the corpus has methods with 20,000 statements)
			var instrOffsets = body.Instructions.Select(i => i.Offset).ToArray();
			IEnumerable<Instruction> InstructionsIn(ILSpan span) {
				int index = System.Array.BinarySearch(instrOffsets, span.Start);
				if (index < 0)
					index = ~index;
				for (; index < instrOffsets.Length && instrOffsets[index] < span.End; index++)
					yield return body.Instructions[index];
			}
			foreach (var statement in info.Statements) {
				var il = statement.ILSpan;
				var ts = statement.TextSpan;
				if (!offsets.Contains(il.Start))
					report.Errors.Add($"Statement {statement} starts inside an instruction");
				if (il.End != codeSize && !offsets.Contains(il.End))
					report.Errors.Add($"Statement {statement} ends inside an instruction");
				if (il.IsEmpty || il.End > codeSize)
					report.Errors.Add($"Statement {statement} has an invalid IL span (code size 0x{codeSize:X})");
				if (ts.Start < 0 || ts.End > text.Length || ts.IsEmpty)
					report.Errors.Add($"Statement {statement} has an invalid text span (text length {text.Length})");
				else if (string.IsNullOrWhiteSpace(text.Substring(ts.Start, ts.Length)))
					report.Errors.Add($"Statement {statement} only covers whitespace");
				// Constructors are exempt: field initializers (and primary constructor parameters) are decompiled
				// outside the constructor's body but their IL is in the constructor
				if (info.HasSpan && !info.Method.IsConstructor && (ts.Start < info.Span.Start || ts.End > info.Span.End))
					report.Errors.Add($"Statement {statement} is outside the method's text span {info.Span}");
				if (stackDepths.TryGetValue(il.Start, out int depth) && depth != 0)
					report.NonEmptyStackStarts++;

				foreach (var instr in InstructionsIn(il)) {
					if (ilOwner.TryGetValue(instr.Offset, out var other) && other != ts)
						report.Errors.Add($"IL_{instr.Offset:X4} belongs to two different statements: {other} and {ts}");
					ilOwner[instr.Offset] = ts;
				}
			}
			var statementAt = new Dictionary<uint, SourceStatement>();
			foreach (var statement in info.Statements) {
				foreach (var instr in InstructionsIn(statement.ILSpan))
					statementAt[instr.Offset] = statement;
			}

			// dnSpy binds a breakpoint to the first instruction of the statement's lowest IL span only
			// (MethodDebugInfo.GetSourceStatementByTextOffset). If the statement can be entered elsewhere from other
			// code, the breakpoint misses those executions or fires when the statement doesn't run (e.g. a 'continue'
			// jump attributed to the next statement). (A branch from the statement's own code is fine: its breakpoint
			// fired already. Not checked: entries from IL without a statement.)
			var breakpointOf = new Dictionary<TextSpan, uint>();
			foreach (var statement in info.Statements) {
				if (!breakpointOf.TryGetValue(statement.TextSpan, out uint bp) || statement.ILSpan.Start < bp)
					breakpointOf[statement.TextSpan] = statement.ILSpan.Start;
			}
			foreach (var (target, source) in entries) {
				if (!statementAt.TryGetValue(target, out var statement) || target == breakpointOf[statement.TextSpan])
					continue;
				if (source is uint s && ilOwner.TryGetValue(s, out var sourceText) && sourceText == statement.TextSpan)
					continue;
				// Inside a piece, or at the start of a piece that isn't the statement's first one, from other code
				if (target != statement.ILSpan.Start || source is not uint src || ilOwner.ContainsKey(src))
					report.Errors.Add($"Statement {statement} can be entered at IL_{target:X4} " + (source is uint from ? $"from IL_{from:X4}" : "(exception handler)") + $" without executing its breakpoint's instruction IL_{breakpointOf[statement.TextSpan]:X4}");
			}
			// The same for a piece that's not the statement's first one and that other code falls into
			var instrList = body.Instructions;
			for (int i = 1; i < instrList.Count; i++) {
				uint offset = instrList[i].Offset;
				if (!statementAt.TryGetValue(offset, out var statement) || offset != statement.ILSpan.Start || offset == breakpointOf[statement.TextSpan])
					continue;
				var prev = instrList[i - 1];
				if (prev.OpCode.FlowControl is FlowControl.Branch or FlowControl.Return or FlowControl.Throw || prev.OpCode.Code is Code.Endfinally or Code.Endfilter)
					continue;
				if (ilOwner.TryGetValue(prev.Offset, out var prevText) && prevText != statement.TextSpan)
					report.Errors.Add($"Statement {statement} is entered at IL_{offset:X4} from IL_{prev.Offset:X4} (the previous statement) without executing its breakpoint's instruction IL_{breakpointOf[statement.TextSpan]:X4}");
			}

			// A simple statement (not an if/loop/switch header) only branches within itself or to its end (e.g.
			// a?.M()). A conditional branch elsewhere means it starts with another statement's code, e.g. the tail of an
			// if condition attributed to the 'break' in its body: a breakpoint on it would fire when it doesn't run. The
			// end of a break/continue/goto is where it jumps anyway, so it can't be a target.
			var kinds = new Dictionary<TextSpan, StatementKind?>();
			foreach (var instr in body.Instructions) {
				if (instr.OpCode.FlowControl != FlowControl.Cond_Branch || !statementAt.TryGetValue(instr.Offset, out var statement))
					continue;
				var il = statement.ILSpan;
				var ts = statement.TextSpan;
				if (!kinds.TryGetValue(ts, out var kind))
					kinds[ts] = kind = ts.Start >= 0 && ts.End <= text.Length ? GetStatementKind(text, ts) : null;
				if (kind is null || kind == StatementKind.ControlFlow)
					continue;
				var targets = instr.Operand is Instruction t ? new[] { t } : instr.Operand as IList<Instruction> ?? System.Array.Empty<Instruction>();
				foreach (var target in targets) {
					bool inside = target.Offset >= il.Start && (target.Offset < il.End || (target.Offset == il.End && kind != StatementKind.Jump));
					// A branch to IL without a statement can be the statement's own code (e.g. the arms of a conditional
					// expression that the decompiler didn't attribute)
					if (!inside && ilOwner.TryGetValue(target.Offset, out var targetText) && targetText != ts)
						report.Errors.Add($"Statement {statement} contains another statement's branch (IL_{instr.Offset:X4} to IL_{target.Offset:X4})");
				}
			}

			// The expression evaluator looks up locals by name in the scopes that contain the IL offset: two locals
			// with the same name in one scope make a name ambiguous (Watch/hover could show the wrong one)
			foreach (var scope in GetScopes(info.Scope)) {
				foreach (var g in scope.Locals.Where(l => !string.IsNullOrEmpty(l.Name)).GroupBy(l => l.Name)) {
					var slots = g.Select(l => l.Local?.Index.ToString() ?? l.HoistedField?.Name.String ?? "?").Distinct().ToArray();
					if (slots.Length > 1)
						report.Errors.Add($"Local name '{g.Key}' is used by {slots.Length} different locals ({string.Join(", ", slots)}) in one scope");
				}
			}

			foreach (var p in info.Parameters) {
				var method = p.Parameter.Method;
				if (method != info.Method && method != info.KickoffMethod)
					report.Errors.Add($"Parameter '{p.Name}' belongs to another method ({method.FullName})");
				else if (!p.Parameter.IsHiddenThisParameter && p.Parameter.Name != p.Name &&
					p.Parameter.Method.Parameters.Any(other => other != p.Parameter && !other.IsHiddenThisParameter && other.Name == p.Name)) {
					// The decompiler may rename parameters (conflicts, discards, unnamed parameters), but a name that
					// belongs to another parameter means the index mapping is off
					report.Errors.Add($"Parameter #{p.Parameter.Index} '{p.Parameter.Name}' is shown as '{p.Name}', the name of another parameter");
				}
			}

			if (info.KickoffMethod is not null && info.Method.DeclaringType is TypeDef stateMachine) {
				var hoistedByField = GetLocals(info.Scope).Where(l => l.HoistedField is not null).GroupBy(l => l.HoistedField!).ToDictionary(g => g.Key, g => g.First());
				foreach (var field in stateMachine.Fields) {
					var m = System.Text.RegularExpressions.Regex.Match(field.Name.String, @"^<([A-Za-z_][A-Za-z0-9_]*)>5__\d+$");
					if (!m.Success)
						continue;
					report.HoistedLocals++;
					if (hoistedByField.TryGetValue(field, out var local)) {
						report.HoistedLocalsMapped++;
						if (local.Name == m.Groups[1].Value)
							report.HoistedLocalsNamed++;
					}
				}
			}

			report.Instructions = stackDepths.Count;
			report.CoveredInstructions = stackDepths.Keys.Count(o => ilOwner.ContainsKey(o));

			if (pdb is not null) {
				if (pdb.KickoffMethodToken != 0 && info.KickoffMethod?.MDToken.ToInt32() != pdb.KickoffMethodToken)
					report.Errors.Add($"Kickoff method is {info.KickoffMethod?.FullName ?? "null"}, the PDB says 0x{pdb.KickoffMethodToken:X8}");
				bool asyncVoid = info.KickoffMethod?.MethodSig?.RetType.RemovePinnedAndModifiers().GetElementType() == ElementType.Void;
				if (asyncVoid && pdb.CatchHandlerOffset is int catchHandler && info.AsyncInfo?.CatchHandlerOffset != (uint)catchHandler)
					report.Errors.Add($"Async void catch handler is IL_{info.AsyncInfo?.CatchHandlerOffset:X4}, the PDB says IL_{catchHandler:X4}");

				var starts = new HashSet<uint>(info.Statements.Select(s => s.ILSpan.Start));
				report.PdbSequencePoints = pdb.SequencePointOffsets.Count;
				report.PdbSequencePointsMatched = pdb.SequencePointOffsets.Count(o => starts.Contains((uint)o));

				if (originalSource is not null) {
					var lineStarts = GetLineStarts(originalSource);
					var variableNames = GetVariableNames(info, pdb);
					// (And the variables the decompiled code declares that aren't IL locals, e.g. values kept on the stack)
					if (info.HasSpan && info.Span.End <= text.Length) {
						foreach (System.Text.RegularExpressions.Match m in declaration.Matches(text.Substring(info.Span.Start, info.Span.Length)))
							variableNames.Add(m.Groups[1].Value);
					}
					foreach (var statement in info.Statements) {
						if (!pdb.SourceLocations.TryGetValue((int)statement.ILSpan.Start, out var loc))
							continue;
						var original = Substring(originalSource, lineStarts, loc);
						// A constructor's declaration is its implicit base constructor call, which isn't in the decompiled code
						if (info.Method.IsConstructor && original.Contains(info.Method.DeclaringType.Name.String.Split('`')[0] + "(", System.StringComparison.Ordinal))
							continue;
						var decompiled = text.Substring(statement.TextSpan.Start, statement.TextSpan.Length);
						var same = IsSameCode(original, decompiled, variableNames);
						if (same is null)
							continue;
						report.SharedLocations++;
						if (same.Value)
							report.SharedLocationsSameCode++;
						else
							report.WrongCode.Add($"IL_{statement.ILSpan.Start:X4}: source `{original.Trim()}` vs decompiled `{decompiled.Trim()}`");
					}
				}

				var allPoints = pdb.SequencePointOffsets.Select(o => (offset: o, hidden: false))
					.Concat(pdb.HiddenSequencePointOffsets.Select(o => (offset: o, hidden: true))).OrderBy(p => p.offset).ToList();
				for (int i = 0; i < allPoints.Count; i++) {
					if (allPoints[i].hidden)
						continue;
					uint start = (uint)allPoints[i].offset;
					uint end = i + 1 < allPoints.Count ? (uint)allPoints[i + 1].offset : codeSize;
					foreach (var offset in stackDepths.Keys) {
						if (offset < start || offset >= end)
							continue;
						report.PdbUserCodeInstructions++;
						if (ilOwner.ContainsKey(offset))
							report.PdbUserCodeInstructionsCovered++;
						else
							report.UncoveredUserCode.Add(offset);
					}
				}

				// The expression evaluator finds captured variables through the display class local's name
				foreach (var kv in pdb.LocalNames) {
					if (!kv.Value.StartsWith("CS$<>8__locals", System.StringComparison.Ordinal))
						continue;
					var ours = GetLocals(info.Scope).FirstOrDefault(l => l.Local?.Index == kv.Key);
					if (ours is null || !ours.Name.StartsWith("CS$<>8__locals", System.StringComparison.Ordinal))
						report.Errors.Add($"Display class local V_{kv.Key} is {(ours is null ? "missing" : $"named '{ours.Name}'")}: the expression evaluator can't find the captured variables");
				}

				var mappedSlots = new HashSet<int>(GetLocals(info.Scope).Where(l => l.Local is not null).Select(l => l.Local!.Index));
				report.PdbLocals = pdb.LocalNames.Count;
				report.PdbLocalsMapped = pdb.LocalNames.Keys.Count(slot => mappedSlots.Contains(slot));
				report.UnmappedPdbLocals.AddRange(pdb.LocalNames.Keys.Where(slot => !mappedSlots.Contains(slot)));

				if (pdb.Awaits.Count != 0) {
					var expected = pdb.Awaits.Select(a => ((uint)a.yieldOffset, (uint)a.resumeOffset)).OrderBy(a => a).ToList();
					var actual = (info.AsyncInfo?.StepInfos ?? System.Array.Empty<AsyncStepInfo>()).Select(a => (a.YieldOffset, a.ResumeOffset)).OrderBy(a => a).ToList();
					if (!expected.SequenceEqual(actual))
						report.Errors.Add($"Async step info differs from the compiler's PDB: expected [{string.Join(", ", expected)}], actual [{string.Join(", ", actual)}]");
				}
			}
			return report;
		}

		enum StatementKind { Simple, Jump, ControlFlow }

		static readonly System.Text.RegularExpressions.Regex controlFlowStatement = new System.Text.RegularExpressions.Regex(@"^(if|while|for|foreach|switch|do|when|catch|using|lock|fixed|in|case|else|try|finally)\b|^[{}]");
		static readonly System.Text.RegularExpressions.Regex jumpStatement = new System.Text.RegularExpressions.Regex(@"^(break|continue|goto)\b");

		static StatementKind GetStatementKind(string text, TextSpan span) {
			var s = text.Substring(span.Start, span.Length).TrimStart();
			if (jumpStatement.IsMatch(s))
				return StatementKind.Jump;
			if (controlFlowStatement.IsMatch(s))
				return StatementKind.ControlFlow;
			// The condition and iterator of a for loop are separate statements
			int lineStart = text.LastIndexOf('\n', System.Math.Max(0, span.Start - 1)) + 1;
			var line = text.Substring(lineStart, span.Start - lineStart).TrimStart();
			if (line.StartsWith("for ", System.StringComparison.Ordinal) || line.StartsWith("for(", System.StringComparison.Ordinal) ||
				line.StartsWith("foreach ", System.StringComparison.Ordinal) || line.StartsWith("foreach(", System.StringComparison.Ordinal) ||
				line.StartsWith("await foreach ", System.StringComparison.Ordinal))
				return StatementKind.ControlFlow;
			return StatementKind.Simple;
		}

		static List<int> GetLineStarts(string text) {
			var list = new List<int> { 0 };
			for (int i = 0; i < text.Length; i++) {
				if (text[i] == '\n')
					list.Add(i + 1);
			}
			return list;
		}

		static string Substring(string text, List<int> lineStarts, (int startLine, int startColumn, int endLine, int endColumn) loc) {
			int start = lineStarts[loc.startLine - 1] + loc.startColumn - 1;
			int end = lineStarts[loc.endLine - 1] + loc.endColumn - 1;
			return text.Substring(start, System.Math.Max(0, end - start));
		}

		/// <summary>
		/// Names of locals and parameters in the original source and in the decompiled code. The decompiler renames
		/// locals (it doesn't get the PDB), so these names aren't used to compare statements.
		/// </summary>
		static readonly System.Text.RegularExpressions.Regex declaration = new System.Text.RegularExpressions.Regex(@"\b(?:var|int|long|bool|string|object|char|double|float|decimal|byte|short|uint|ulong|[A-Z]\w*(?:<[^;=()]*?>)?(?:\[\])?\??)\s+([a-z_]\w*)\s*[=;)]");

		internal static HashSet<string> GetVariableNames(MethodDebugInfo info, PdbMethodInfo pdb) {
			var names = new HashSet<string>(pdb.LocalNames.Values);
			foreach (var method in new[] { info.Method, info.KickoffMethod }) {
				if (method is null)
					continue;
				foreach (var p in method.Parameters)
					names.Add(p.Name);
			}
			foreach (var l in GetLocals(info.Scope))
				names.Add(l.Name);
			foreach (var p in info.Parameters)
				names.Add(p.Name);
			// Hoisted locals (<name>5__N) and captured variables live in compiler generated types
			for (var type = info.Method.DeclaringType; type is not null; type = type.DeclaringType) {
				foreach (var nested in type.NestedTypes.Append(type)) {
					if (!nested.Name.String.Contains("<"))
						continue;
					foreach (var field in nested.Fields) {
						var m = System.Text.RegularExpressions.Regex.Match(field.Name.String, @"^<([A-Za-z_][A-Za-z0-9_]*)>5__\d+$");
						names.Add(m.Success ? m.Groups[1].Value : field.Name.String);
					}
				}
			}
			return names;
		}

		/// <summary>
		/// Are the original and decompiled statements about the same code? They are if they share a member name or
		/// literal. Returns null if there's nothing to compare: braces (the decompiler has no brace statements, so it
		/// attributes a brace's nop to the next statement) or statements that only use variables.
		/// </summary>
		internal static bool? IsSameCode(string original, string decompiled, HashSet<string> variableNames) {
			if (IsBrace(original) || IsBrace(decompiled))
				return null;
			var a = Words(original, variableNames);
			var b = Words(decompiled, variableNames);
			if (a.Count == 0 || b.Count == 0)
				return null;
			// Constant expressions are folded by the compiler (int.MaxValue - 1 is decompiled as 2147483646)
			if (a.All(IsNumber) || b.All(IsNumber))
				return null;
			// At least half of the smaller set must be shared (one shared name like 'Console' isn't enough)
			int shared = a.Count(w => b.Contains(w));
			return shared * 2 >= System.Math.Min(a.Count, b.Count);
		}

		static bool IsBrace(string s) => s.Trim() is "{" or "}";

		static bool IsNumber(string word) => word.Length != 0 && char.IsDigit(word[0]);

		static readonly HashSet<string> keywords = new HashSet<string> { "var", "int", "string", "object", "bool", "return", "new", "this", "base", "in", "is", "when", "if", "else", "for", "foreach", "while", "do", "using", "try", "catch", "finally", "throw", "await", "yield", "break", "continue", "lock", "checked", "switch", "case", "default", "null", "true", "false", "async" };

		static HashSet<string> Words(string s, HashSet<string> variableNames) {
			var set = new HashSet<string>();
			foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(s, @"[A-Za-z_][A-Za-z0-9_]*|\d+|""[^""]*""")) {
				if (!keywords.Contains(m.Value) && !variableNames.Contains(m.Value))
					set.Add(m.Value);
			}
			return set;
		}

		static IEnumerable<MethodDebugScope> GetScopes(MethodDebugScope scope) {
			yield return scope;
			foreach (var child in scope.Scopes) {
				foreach (var s in GetScopes(child))
					yield return s;
			}
		}

		static IEnumerable<SourceLocal> GetLocals(MethodDebugScope scope) {
			foreach (var local in scope.Locals)
				yield return local;
			foreach (var child in scope.Scopes) {
				foreach (var local in GetLocals(child))
					yield return local;
			}
		}
	}

	/// <summary>
	/// Control transfers that don't fall through: (target, source) for each branch, and (start, null) for each
	/// exception handler and filter
	/// </summary>
	static class ControlFlowEntries {
		public static List<(uint target, uint? source)> Calculate(CilBody body) {
			var list = new List<(uint, uint?)>();
			foreach (var instr in body.Instructions) {
				if (instr.Operand is Instruction target)
					list.Add((target.Offset, instr.Offset));
				else if (instr.Operand is IList<Instruction> targets) {
					foreach (var t in targets)
						list.Add((t.Offset, instr.Offset));
				}
			}
			foreach (var eh in body.ExceptionHandlers) {
				if (eh.HandlerStart is not null)
					list.Add((eh.HandlerStart.Offset, null));
				if (eh.FilterStart is not null)
					list.Add((eh.FilterStart.Offset, null));
			}
			return list;
		}
	}

	/// <summary>
	/// IL evaluation stack depth at each reachable instruction
	/// </summary>
	static class StackDepthCalculator {
		public static Dictionary<uint, int> Calculate(MethodDef method) {
			var body = method.Body;
			var result = new Dictionary<uint, int>();
			var work = new Stack<(Instruction instr, int depth)>();
			bool hasReturnValue = method.MethodSig.RetType.RemovePinnedAndModifiers().GetElementType() != ElementType.Void;
			if (body.Instructions.Count != 0)
				work.Push((body.Instructions[0], 0));
			foreach (var eh in body.ExceptionHandlers) {
				if (eh.HandlerStart is not null)
					work.Push((eh.HandlerStart, eh.IsCatch || eh.IsFilter ? 1 : 0));
				if (eh.FilterStart is not null)
					work.Push((eh.FilterStart, 1));
			}
			var indexes = new Dictionary<Instruction, int>();
			for (int i = 0; i < body.Instructions.Count; i++)
				indexes[body.Instructions[i]] = i;

			while (work.Count != 0) {
				var (instr, depth) = work.Pop();
				if (result.ContainsKey(instr.Offset))
					continue;
				result[instr.Offset] = depth;
				instr.CalculateStackUsage(hasReturnValue, out int pushes, out int pops);
				int after = pops == -1 ? 0 : depth - pops + pushes;
				if (after < 0)
					after = 0;
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					if (instr.Operand is Instruction target)
						work.Push((target, instr.OpCode.Code == Code.Leave || instr.OpCode.Code == Code.Leave_S ? 0 : after));
					continue;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction condTarget)
						work.Push((condTarget, after));
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var t in targets)
							work.Push((t, after));
					}
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					continue;
				}
				if (indexes.TryGetValue(instr, out int index) && index + 1 < body.Instructions.Count)
					work.Push((body.Instructions[index + 1], after));
			}
			return result;
		}
	}
}
