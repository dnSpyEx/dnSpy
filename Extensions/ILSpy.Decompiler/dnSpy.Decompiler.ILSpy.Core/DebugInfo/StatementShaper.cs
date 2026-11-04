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
using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	enum StatementKind {
		/// <summary>Control flow (if/loop/switch headers, catch clauses...) or unknown</summary>
		Other,
		/// <summary>A statement whose expression doesn't branch: expression, declaration, return, throw, yield return</summary>
		Simple,
		/// <summary>A simple statement whose expression can branch (?., ??, ?:, &amp;&amp;, ||, switch expressions,
		/// patterns, await...), e.g. to its end: 'x?.M();' skips the call</summary>
		SimpleBranching,
		/// <summary>break, continue, goto, yield break</summary>
		Jump,
		/// <summary>An opening or closing brace</summary>
		Brace,
	}

	/// <summary>
	/// What the shaper knows about the decompiled code of a method
	/// </summary>
	sealed class ShapeContext {
		public Func<TextSpan, StatementKind> GetKind { get; }
		/// <summary>IL locals that the decompiled code shows as variables (the others are compiler temporaries)</summary>
		public ISet<int> VisibleLocals { get; }
		/// <summary>
		/// IL offsets of the instructions that access the variables of <see cref="VisibleLocals"/>, if known: optimized
		/// code reuses a local's slot, e.g. for a variable and a compiler temporary
		/// </summary>
		public IDictionary<int, HashSet<uint>>? VisibleAccesses { get; set; }

		/// <summary>
		/// Is the IL local a compiler temporary (the decompiled code doesn't show it)? With <paramref name="offset"/>, is
		/// that access of the local one that the decompiled code doesn't show (upstream doesn't keep the IL offsets of
		/// all accesses, so this can be wrong for a visible variable)?
		/// </summary>
		public bool IsTemporary(int local, uint? offset = null) {
			if (!VisibleLocals.Contains(local))
				return true;
			return offset is uint o && VisibleAccesses is not null && VisibleAccesses.TryGetValue(local, out var offsets) && offsets.Count != 0 && !offsets.Contains(o);
		}
		/// <summary>Closing brace of the method's body, if it has a block body</summary>
		public TextSpan? ClosingBrace { get; }
		/// <summary>The method is a state machine's MoveNext (its 'ret' isn't the end of the decompiled method)</summary>
		public bool IsStateMachine { get; }
		/// <summary>
		/// Opening brace of a constructor whose base constructor call isn't in the decompiled text ('base()' is implicit)
		/// </summary>
		public TextSpan? ImplicitBaseCallBrace { get; set; }

		public ShapeContext(Func<TextSpan, StatementKind> getKind, ISet<int> visibleLocals, TextSpan? closingBrace, bool isStateMachine) {
			GetKind = getKind;
			VisibleLocals = visibleLocals;
			ClosingBrace = closingBrace;
			IsStateMachine = isStateMachine;
		}
	}

	/// <summary>
	/// Fixes the IL spans of statements so that breakpoints, stepping and the current statement work like they do
	/// with a compiler's PDB. Statements whose IL is already well formed are left unchanged, so this is a no-op if
	/// upstream's sequence points get better.
	/// </summary>
	/// <remarks>
	/// <para>How dnSpy uses the statements: a breakpoint is bound to the first instruction of the statement's
	/// lowest IL span only (MethodDebugInfo.GetSourceStatementByTextOffset returns the first statement in IL
	/// order), and only if the JIT has a boundary there (where the stack is empty, after a call or a nop, at the start
	/// of the method and of a handler or filter; the runtime doesn't bind a breakpoint anywhere else). Step over
	/// runs until the IL leaves the statement's spans and any IL without a statement. A frame's current statement
	/// is the one whose span contains its IL offset.</para>
	/// <para>So a statement's lowest span must be its entry: executed whenever the statement runs, before its other
	/// spans, and not when it doesn't run. ICSharpCode.Decompiler 11.2 produces spans that break this:</para>
	/// <list type="bullet">
	/// <item>A branch of another statement (a 'continue', the jump to a switch's default case, a loop's entry
	/// jump) is attributed to the next statement, so the span starts with an instruction that jumps away and its
	/// real code is entered from elsewhere. Fix: split spans at every instruction that can be entered from outside
	/// the statement, drop the pieces that only jump or that are in another exception handling block than the
	/// statement's own code.</item>
	/// <item>The tail of a condition ('ldloc; brtrue' of an if or do-while) is attributed to the next statement:
	/// a breakpoint on a 'break' or 'throw' fires each time the condition is tested. Fix: a simple statement can't
	/// start with a conditional branch to another statement's code; such a prefix goes to the previous statement
	/// if it's a control flow header, else it's hidden.</item>
	/// <item>Other statements' leftovers before a statement's own code (the GetResult() of the previous await, the
	/// arm of the previous statement's conditional expression, a filter's 'false' result): fix: of a statement's
	/// pieces, only the entry (the piece that dominates the others in the control flow graph) and the pieces after
	/// it that it dominates are kept.</item>
	/// <item>The end of an expression is attributed to the next statement or a brace (the call of 'x?.M()', the
	/// store of 'x = v ?? d;'), and calls of transformed code (cached delegates, display class fields) aren't
	/// attributed, so a frame stopped in those calls has no current statement. Fix: IL that continues an
	/// expression (it pops the values the statement left on the stack) goes back to the statement, and statements
	/// are extended over unattributed IL while the stack isn't empty or while it reads a compiler temporary the
	/// statement stored. A brace never computes values: its IL that leaves values on the stack goes to the
	/// statement that uses them (e.g. 'stackalloc int[4]''s size given to the method's '{').</item>
	/// <item>The epilogue of a method (load the return value, ret) isn't attributed: it's the closing brace, where
	/// a step over the last statement stops.</item>
	/// <item>Code that's only reached from a statement and continues in it (the arms of a switch expression, the
	/// creation of a cached delegate) or that only a brace has (the rest of an 'if' condition, a lock's
	/// Monitor.Enter()) belongs to that statement; an implicit base constructor call to the constructor's '{'.</item>
	/// </list>
	/// </remarks>
	static class StatementShaper {
		/// <summary>
		/// Tests: called when shaping a method failed (its statements are used unchanged)
		/// </summary>
		internal static Action<MethodDef, Exception>? OnError;

		/// <summary>
		/// Tests: returns upstream's statements unchanged (flows into the decompiler's thread)
		/// </summary>
		internal static readonly AsyncLocal<bool> Disabled = new AsyncLocal<bool>();

		/// <summary>
		/// Diagnostics: gets the statements after each step
		/// </summary>
		internal static readonly AsyncLocal<Action<string>?> Trace = new AsyncLocal<Action<string>?>();

		public static List<SourceStatement> Shape(List<SourceStatement> statements, MethodDef method, ShapeContext context) {
			if (statements.Count == 0 || Disabled.Value)
				return statements;
			var body = method.Body;
			if (body is null || body.Instructions.Count == 0)
				return statements;
			try {
				var flow = MethodFlow.TryCreate(method);
				if (flow is null)
					return statements;
				var trace = Trace.Value;
				trace?.Invoke($"== {method.FullName}");
				var shaper = new Shaper(method, flow, context, trace);
				if (!shaper.Init(statements))
					return statements;
				return shaper.Run();
			}
			catch (Exception ex) when (ex is not OutOfMemoryException) {
				// Never fail the decompilation because of invalid IL: keep upstream's statements
				OnError?.Invoke(method, ex);
				return statements;
			}
		}

		sealed class Shaper {
			readonly MethodDef method;
			readonly MethodFlow flow;
			readonly ShapeContext context;
			readonly Action<string>? trace;
			readonly int count;
			List<(int start, int end, TextSpan text)> spans = new List<(int start, int end, TextSpan text)>();
			// Index into 'spans' of the piece that owns each instruction, or -1
			readonly int[] owner;

			public Shaper(MethodDef method, MethodFlow flow, ShapeContext context, Action<string>? trace) {
				this.method = method;
				this.flow = flow;
				this.context = context;
				this.trace = trace;
				count = flow.Count;
				owner = new int[count];
			}

			public bool Init(List<SourceStatement> statements) {
				for (int i = 0; i < owner.Length; i++)
					owner[i] = -1;
				foreach (var s in statements.OrderBy(a => a.ILSpan.Start)) {
					if (!flow.TryGetIndex(s.ILSpan.Start, out int start) || !flow.TryGetIndex(s.ILSpan.End, out int end) || end <= start)
						return false;
					for (int i = start; i < end; i++) {
						if (owner[i] >= 0)
							return false;
						owner[i] = spans.Count;
					}
					spans.Add((start, end, s.TextSpan));
				}
				return true;
			}

			public List<SourceStatement> Run() {
				Log("upstream");
				GiveImplicitBaseCallToBrace();
				// Pieces are entered at their start only. Splitting reveals more continuations (e.g. the arm of a
				// conditional expression that another statement got), so do it twice.
				for (int pass = 0; pass < 2; pass++) {
					// A statement that starts in the middle of the previous one's expression is fixed first, so the
					// extension knows where each statement's expression starts
					ReclaimExpressionContinuations();
					Log("reclaim");
					ExtendStatements();
					Log("extend");
					SplitAtEntries();
					Log("split");
					// (Before reassigning pieces: a piece entered from a condition's tail isn't the condition's code)
					MoveConditionTails();
					Log("tails");
					ReassignPiecesOfOtherStatements();
					Log("reassign");
					// (After splitting: a jump that upstream gave to another statement isn't a statement's own branch)
					ExtendOverArms();
					Log("arms");
				}
				AddEpilogue();
				Log("epilogue");
				// (A condition can end with a branch to the epilogue)
				MoveConditionTails();
				Log("tails");
				SplitUntilStable();
				KeepEntries();
				Log("entries");
				StartAtBoundaries();
				Log("boundaries");
				SplitUntilStable();
				GiveReturnAddressesToCalls();
				Log("result");

				var result = new List<SourceStatement>(spans.Count);
				foreach (var (start, end, text) in spans) {
					if (end > start)
						result.Add(new SourceStatement(flow.ToILSpan(start, end), text));
				}
				return result;
			}

			StatementKind KindOf(TextSpan text) => context.GetKind(text);
			bool IsBrace(TextSpan text) => KindOf(text) == StatementKind.Brace;

			/// <summary>
			/// Is it the same statement or one nested in it (e.g. an arm of a switch expression)?
			/// </summary>
			static bool IsPartOf(TextSpan inner, TextSpan outer) => outer.Start <= inner.Start && inner.End <= outer.End;
			static bool IsSameFamily(TextSpan a, TextSpan b) => IsPartOf(a, b) || IsPartOf(b, a);

			void Log(string step) {
				// Each instruction belongs to one statement at most (else the input is used)
				var sorted = spans.Where(a => a.end > a.start).OrderBy(a => a.start).ToList();
				for (int i = 1; i < sorted.Count; i++) {
					if (sorted[i].start < sorted[i - 1].end)
						throw new InvalidOperationException($"Statements overlap after '{step}': IL_{flow.Offsets[sorted[i].start]:X4}");
				}
				if (trace is null)
					return;
				var sb = new StringBuilder();
				sb.Append("  ").Append(step).Append(':');
				foreach (var (start, end, text) in spans.OrderBy(a => a.start))
					sb.Append($" [{flow.Offsets[start]:X4},{flow.Offsets[end]:X4}){text.Start}");
				trace(sb.ToString());
			}

			/// <summary>
			/// The base constructor call that isn't in the decompiled text is the code of the constructor's '{' (the
			/// compiler maps it to the declaration): upstream gives it to the first statement (in optimized code, all but
			/// its 'ldarg.0', which stays with the '{'), where a breakpoint would stop before the base constructor ran.
			/// Not if a statement before the '{' has it (e.g. the constructor's declaration, like the compiler).
			/// </summary>
			void GiveImplicitBaseCallToBrace() {
				if (context.ImplicitBaseCallBrace is not TextSpan brace || !method.IsInstanceConstructor || method.DeclaringType is not TypeDef type || type.IsValueType)
					return;
				var comparer = new SigComparer();
				int call = -1;
				for (int i = 0; i < count; i++) {
					var instr = flow.Instructions[i];
					if (instr.OpCode.Code == Code.Call && instr.Operand is IMethod callee && callee.Name == ".ctor" &&
						(comparer.Equals(callee.DeclaringType, type) || comparer.Equals(callee.DeclaringType, type.BaseType))) {
						call = i;
						break;
					}
				}
				if (call < 0)
					return;
				// Its arguments ('this' first) are pushed on an empty stack
				int start = call;
				while (start > 0 && flow.StackDepth[start] > 0)
					start--;
				if (flow.StackDepth[start] != 0)
					return;
				for (int i = start; i <= call; i++) {
					if (flow.Block[i] != flow.Block[start] || (owner[i] >= 0 && spans[owner[i]].text.Start < brace.Start))
						return;
				}
				var result = new List<(int start, int end, TextSpan text)>(spans.Count + 2);
				foreach (var (pStart, pEnd, text) in spans) {
					if (pEnd <= start || pStart > call) {
						result.Add((pStart, pEnd, text));
						continue;
					}
					if (pStart < start)
						result.Add((pStart, start, text));
					if (pEnd > call + 1)
						result.Add((call + 1, pEnd, text));
				}
				result.Add((start, call + 1, brace));
				spans = result;
				MergeAdjacentPieces();
				Log("base call");
			}

			void RebuildOwners() {
				spans.RemoveAll(a => a.end <= a.start);
				spans.Sort((a, b) => a.start.CompareTo(b.start));
				for (int i = 0; i < owner.Length; i++)
					owner[i] = -1;
				for (int si = 0; si < spans.Count; si++) {
					for (int i = spans[si].start; i < spans[si].end; i++)
						owner[i] = si;
				}
			}

			Dictionary<TextSpan, int> CountPieces() {
				var pieceCount = new Dictionary<TextSpan, int>();
				foreach (var span in spans)
					pieceCount[span.text] = pieceCount.TryGetValue(span.text, out int n) ? n + 1 : 1;
				return pieceCount;
			}

			IEnumerable<int> Predecessors(int index) {
				if (index > 0 && MethodFlow.FallsThrough(flow.Instructions[index - 1]))
					yield return index - 1;
				foreach (int source in flow.BranchSources(index))
					yield return source;
			}

			/// <summary>
			/// Upstream sometimes attributes the end of an expression to another statement, e.g. the call and the null
			/// path of 'Error?.Invoke(this, e);' or the store of 'x = v ?? d;' to the method's closing brace (its
			/// implicit return gets the IL that transforms removed). IL that continues an expression (it pops values
			/// that the statement left on the stack) goes back to the statement it's reached from. The brace's
			/// instructions between two pieces of a statement go to the statement too, so it's one span like the
			/// compiler's (one breakpoint location).
			/// </summary>
			void ReclaimExpressionContinuations() {
				var pieceCount = CountPieces();
				bool changed = false;
				int n = spans.Count;
				for (int si = 0; si < n; si++) {
					var (start, end, text) = spans[si];
					if (end <= start || flow.StackDepth[start] <= 0 || flow.IsHandlerStart[start] || !ConsumesStack(start, end))
						continue;
					int gapStart = start;
					int continued = FindContinuedStatement(ref gapStart, text);
					if (continued < 0)
						continue;
					var (cStart, cEnd, cText) = spans[continued];
					if (cEnd <= cStart)
						continue;
					int baseDepth = flow.StackDepth[cStart];
					// It continues the expression only if it uses values the statement pushed (else they're values that
					// optimized code keeps on the stack across statements): the statement's code before it (and before
					// the IL without a statement in between) left values on the stack
					if (baseDepth < 0 || flow.StackDepth[start] <= baseDepth || flow.StackDepth[gapStart] <= baseDepth)
						continue;
					if (IsBrace(cText) && !IsBrace(text)) {
						// A brace doesn't compute values: its IL that leaves values on the stack is the start of this
						// statement's expression
						if (cEnd != gapStart)
							continue;
						int cut = cEnd - 1;
						while (cut > cStart && flow.StackDepth[cut] != baseDepth)
							cut--;
						if (flow.StackDepth[cut] != baseDepth)
							continue;
						// Unless that IL starts a try block after the statement before the brace: it's that statement's
						// code (a lock statement's Monitor.Enter(), like the compiler maps it), the brace's IL too
						int header = cStart > 0 ? owner[cStart - 1] : -1;
						if (header >= 0 && header != si && !IsBrace(spans[header].text) && spans[header].end == cStart &&
							flow.Block[cut] != flow.Block[cStart] && !flow.IsInHandler(cut)) {
							int headerEnd = start;
							while (headerEnd < end && (headerEnd == start || flow.StackDepth[headerEnd] > baseDepth))
								headerEnd++;
							if (headerEnd < end) {
								spans[header] = (spans[header].start, headerEnd, spans[header].text);
								spans[continued] = (cStart, cStart, cText);
								spans[si] = (headerEnd, end, text);
								for (int j = cStart; j < headerEnd; j++)
									owner[j] = header;
								changed = true;
								continue;
							}
						}
						spans[continued] = (cStart, cut, cText);
						spans[si] = (cut, end, text);
						for (int j = cut; j < start; j++)
							owner[j] = si;
						changed = true;
						continue;
					}
					int reclaimEnd = ContinuationEnd(start, end, baseDepth);
					// A statement isn't swallowed by another one (a brace can be: upstream gives the closing brace
					// the IL that transforms removed)
					if (reclaimEnd >= end && !IsBrace(text) && pieceCount[text] < 2)
						continue;
					spans.Add((gapStart, reclaimEnd, cText));
					spans[si] = (reclaimEnd, end, text);
					for (int j = gapStart; j < reclaimEnd; j++)
						owner[j] = spans.Count - 1;
					changed = true;
				}
				if (changed)
					RebuildOwners();
				// The same for IL without a statement (e.g. the call of 'x?.M()' in debug code)
				for (int i = 0; i < count; i++) {
					if (owner[i] >= 0 || flow.StackDepth[i] <= 0 || (i > 0 && owner[i - 1] < 0 && !flow.BranchSources(i).Any()))
						continue;
					int runEnd = i;
					while (runEnd < count && owner[runEnd] < 0 && flow.Block[runEnd] == flow.Block[i])
						runEnd++;
					if (!ConsumesStack(i, runEnd))
						continue;
					int gapStart = i;
					int continued = FindContinuedStatement(ref gapStart, default);
					if (continued < 0 || gapStart != i)
						continue;
					if (IsBrace(spans[continued].text)) {
						// A brace doesn't compute values: if the IL after it uses values it left on the stack, it's all the
						// code of the statement before it (e.g. Monitor.Enter() of a lock statement given to its '{')
						var (bStart, bEnd, _) = spans[continued];
						int header = bStart > 0 ? owner[bStart - 1] : -1;
						int braceBase = flow.StackDepth[bStart];
						// (A lock statement's header is before its try block, Monitor.Enter() in it)
						if (header < 0 || IsBrace(spans[header].text) || spans[header].end != bStart || bEnd != i || braceBase < 0 ||
							flow.StackDepth[i] <= braceBase || flow.IsInHandler(i))
							continue;
						int headerEnd = i;
						while (headerEnd < runEnd && (headerEnd == i || flow.StackDepth[headerEnd] > braceBase))
							headerEnd++;
						spans[header] = (spans[header].start, headerEnd, spans[header].text);
						spans[continued] = (bStart, bStart, spans[continued].text);
						for (int j = bStart; j < headerEnd; j++)
							owner[j] = header;
						changed = true;
						continue;
					}
					int baseDepth = flow.StackDepth[spans[continued].start];
					if (baseDepth < 0 || flow.StackDepth[i] <= baseDepth)
						continue;
					int reclaimEnd = ContinuationEnd(i, runEnd, baseDepth);
					spans.Add((i, reclaimEnd, spans[continued].text));
					for (int j = i; j < reclaimEnd; j++)
						owner[j] = spans.Count - 1;
					changed = true;
				}
				if (!changed)
					return;
				RebuildOwners();
				FillBraceGaps();
				MergeAdjacentPieces();
			}

			/// <summary>
			/// End of the code at <paramref name="start"/> that continues an expression: where the stack is back to the
			/// expression's <paramref name="baseDepth"/>, or, once the code has used the values it started with, where
			/// other code joins it (the end of a conditional expression's arm: optimized code can keep the value on the
			/// stack for later statements).
			/// </summary>
			int ContinuationEnd(int start, int end, int baseDepth) {
				int i = start;
				bool consumed = false;
				while (i < end && (i == start || flow.StackDepth[i] > baseDepth)) {
					if (i > start && consumed && flow.BranchSources(i).Any(source => source < start || source >= i))
						break;
					if (flow.StackDepth[i] >= 0) {
						flow.Instructions[i].CalculateStackUsage(flow.HasReturnValue, out _, out int pops);
						if (pops > 0 && flow.StackDepth[i] - pops < flow.StackDepth[start])
							consumed = true;
					}
					i++;
				}
				return i;
			}

			/// <summary>
			/// The statement whose expression the code at <paramref name="start"/> continues: all its predecessors that
			/// belong to a statement belong to that statement. Walks back over IL without a statement that can only be
			/// reached by falling through (<paramref name="start"/> is updated to the start of that IL).
			/// </summary>
			int FindContinuedStatement(ref int start, TextSpan text) {
				int index = start;
				for (int steps = 0; steps < 32; steps++) {
					int found = -1;
					int predecessors = 0;
					bool unowned = false;
					foreach (int pred in Predecessors(index)) {
						predecessors++;
						int o = owner[pred];
						if (o < 0) {
							unowned = true;
							continue;
						}
						if (spans[o].text == text || (found >= 0 && spans[found].text != spans[o].text))
							return -1;
						found = o;
					}
					// (IL without a statement before it must only be reached from the statement)
					if (found >= 0 && (steps == 0 || !unowned)) {
						start = index;
						return found;
					}
					if (found >= 0)
						return -1;
					if (predecessors != 1 || index == 0 || owner[index - 1] >= 0 || flow.IsHandlerStart[index] || flow.BranchSources(index).Any() ||
						!MethodFlow.FallsThrough(flow.Instructions[index - 1]) || flow.Block[index - 1] != flow.Block[index])
						return -1;
					index--;
				}
				return -1;
			}

			/// <summary>
			/// Gives a statement the brace instructions between two of its pieces that are only entered from the first
			/// piece (the null path's ret of 'Error?.Invoke(this, e);')
			/// </summary>
			void FillBraceGaps() {
				const int MaxGap = 4;
				var gaps = new List<(int start, int end, TextSpan text)>();
				foreach (var (_, pieceEnd, text) in spans) {
					if (IsBrace(text) || pieceEnd >= count)
						continue;
					int gapStart = pieceEnd;
					int gapEnd = gapStart;
					while (gapEnd < count && gapEnd - gapStart < MaxGap && owner[gapEnd] >= 0 && IsBrace(spans[owner[gapEnd]].text))
						gapEnd++;
					if (gapEnd == gapStart || gapEnd >= count || owner[gapEnd] < 0 || spans[owner[gapEnd]].text != text)
						continue;
					bool fill = true;
					for (int i = gapStart; fill && i < gapEnd; i++) {
						if (i != gapStart && flow.BranchSources(i).Any())
							fill = false;
						else if (i == gapStart && flow.BranchSources(i).Any(b => owner[b] < 0 || spans[owner[b]].text != text))
							fill = false;
					}
					if (fill)
						gaps.Add((gapStart, gapEnd, text));
				}
				if (gaps.Count == 0)
					return;
				var newText = new TextSpan?[count];
				foreach (var (gapStart, gapEnd, text) in gaps) {
					for (int i = gapStart; i < gapEnd; i++)
						newText[i] = text;
				}
				// Cut the gaps out of the braces' pieces (a piece can lose its start, end or middle)
				var result = new List<(int start, int end, TextSpan text)>(spans.Count + gaps.Count);
				foreach (var span in spans) {
					if (!IsBrace(span.text)) {
						result.Add(span);
						continue;
					}
					int pieceStart = span.start;
					for (int i = span.start; i <= span.end; i++) {
						if (i == span.end || newText[i] is not null) {
							if (i > pieceStart)
								result.Add((pieceStart, i, span.text));
							pieceStart = i + 1;
						}
					}
				}
				result.AddRange(gaps);
				spans = result;
				RebuildOwners();
			}

			/// <summary>
			/// Merges adjacent pieces of a statement, unless the second one is entered from elsewhere
			/// </summary>
			void MergeAdjacentPieces() {
				RebuildOwners();
				var merged = new List<(int start, int end, TextSpan text)>(spans.Count);
				foreach (var span in spans) {
					if (merged.Count != 0) {
						var last = merged[merged.Count - 1];
						if (last.text == span.text && last.end == span.start && !flow.IsHandlerStart[span.start] &&
							!flow.BranchSources(span.start).Any(src => owner[src] < 0 || spans[owner[src]].text != span.text)) {
							merged[merged.Count - 1] = (last.start, span.end, last.text);
							continue;
						}
					}
					merged.Add(span);
				}
				spans = merged;
				RebuildOwners();
			}

			/// <summary>
			/// Extends statements over unattributed IL while the statement's expression is still being evaluated, or
			/// while that IL uses a compiler temporary the statement stored. (Braces don't compute anything.)
			/// </summary>
			void ExtendStatements() {
				for (int si = 0; si < spans.Count; si++) {
					var (start, end, text) = spans[si];
					if (IsBrace(text))
						continue;
					// The stack can hold values of earlier statements (optimized code keeps a variable on the stack)
					int baseDepth = flow.StackDepth[start];
					int newEnd = end;
					bool continues = end < count && baseDepth >= 0 && flow.StackDepth[end] > baseDepth && ConsumesStack(end, count);
					while (newEnd < count && owner[newEnd] < 0 && flow.Block[newEnd] == flow.Block[newEnd - 1]) {
						if (continues && flow.StackDepth[newEnd] > baseDepth) {
							owner[newEnd++] = si;
							continue;
						}
						if (flow.StackDepth[newEnd] != baseDepth)
							break;
						// (Debug code has nops between the parts, e.g. the appends of an interpolated string's handler)
						int runStart = newEnd;
						while (runStart < count && owner[runStart] < 0 && flow.Instructions[runStart].OpCode.Code == Code.Nop && flow.Block[runStart] == flow.Block[newEnd])
							runStart++;
						if (runStart >= count || owner[runStart] >= 0 || flow.Block[runStart] != flow.Block[newEnd])
							break;
						int runEnd = GetTemporaryRunEnd(start, runStart);
						if (runEnd <= runStart)
							break;
						while (newEnd < runEnd)
							owner[newEnd++] = si;
					}
					if (newEnd != end)
						spans[si] = (start, newEnd, text);
				}
			}

			/// <summary>
			/// A statement whose expression branches (?:, ??, throw expressions...) gets the IL without a statement that's
			/// only reached from its code, if that IL calls something (the arms that upstream didn't attribute: a frame in
			/// such a call shows its statement) or if all of it continues at one place (the arms of a switch expression or
			/// of a conditional expression that's a lambda's body: they compute the statement's value). Not an await's
			/// suspension (it stores the state machine's state), which the compiler hides, nor the compiler's hidden
			/// dispatch code of a switch or an if, which continues at several places. Any other statement gets such IL
			/// if it continues in the statement's code (e.g. the creation of a cached delegate in optimized code). Not
			/// the method's epilogue, which is the closing brace's code (see <see cref="AddEpilogue"/>): the 'ret' after
			/// 'x?.M();', the 'ldloc; ret' after 'return a ? b : c;' in debug code.
			/// </summary>
			void ExtendOverArms() {
				bool changed = false;
				var inRegion = new bool[count];
				var epilogue = GetEpilogue();
				for (int si = 0; si < spans.Count; si++) {
					var text = spans[si].text;
					var kind = KindOf(text);
					if (kind == StatementKind.Brace)
						continue;
					// The IL without a statement reachable from the statement's code
					var region = new List<int>();
					var work = new Stack<int>();
					for (int i = spans[si].start; i < spans[si].end; i++) {
						foreach (int next in flow.Successors(i)) {
							if (next < count && owner[next] < 0 && !inRegion[next] && !epilogue[next] && flow.Block[next] == flow.Block[spans[si].start]) {
								inRegion[next] = true;
								work.Push(next);
							}
						}
					}
					while (work.Count != 0) {
						int i = work.Pop();
						region.Add(i);
						foreach (int next in flow.Successors(i)) {
							if (next < count && owner[next] < 0 && !inRegion[next] && !epilogue[next] && flow.Block[next] == flow.Block[i]) {
								inRegion[next] = true;
								work.Push(next);
							}
						}
					}
					// It's only entered from the statement, calls something and doesn't suspend
					bool ok = region.Count != 0;
					bool calls = false;
					int exit = -1;
					bool oneExit = true;
					bool backToStatement = true;
					foreach (int i in region) {
						var instr = flow.Instructions[i];
						if (instr.OpCode.FlowControl == FlowControl.Call || instr.OpCode.Code == Code.Newobj)
							calls = true;
						foreach (int next in flow.Successors(i)) {
							if (next < count && inRegion[next])
								continue;
							if (exit >= 0 && exit != next)
								oneExit = false;
							exit = next;
							if (next >= count || owner[next] < 0 || spans[owner[next]].text != text)
								backToStatement = false;
						}
						if (instr.OpCode.Code == Code.Stfld && instr.Operand is IField field && field.Name == "<>1__state")
							ok = false;
						foreach (int pred in Predecessors(i)) {
							if (!inRegion[pred] && (owner[pred] < 0 || spans[owner[pred]].text != text))
								ok = false;
						}
					}
					if (ok && (kind == StatementKind.SimpleBranching ? calls || oneExit : backToStatement && exit >= 0)) {
						region.Sort();
						int runStart = region[0];
						for (int k = 1; k <= region.Count; k++) {
							if (k == region.Count || region[k] != region[k - 1] + 1) {
								spans.Add((runStart, region[k - 1] + 1, text));
								if (k < region.Count)
									runStart = region[k];
							}
						}
						foreach (int i in region)
							owner[i] = spans.Count - 1;
						changed = true;
					}
					foreach (int i in region)
						inRegion[i] = false;
				}
				if (changed)
					MergeAdjacentPieces();
			}

			/// <summary>
			/// Splits the statements at their extra entry points, keeping the pieces with the statement's own code
			/// </summary>
			void SplitAtEntries() {
				var textOf = new TextSpan?[count];
				foreach (var (start, end, text) in spans) {
					for (int i = start; i < end; i++)
						textOf[i] = text;
				}
				var result = new List<(int start, int end, TextSpan text)>(spans.Count);
				foreach (var (start, end, text) in spans) {
					var pieces = Split(start, end, text, textOf);
					if (pieces.Count == 1) {
						result.Add((start, end, text));
						continue;
					}
					// The statement's own code is the last piece (upstream attributes leftover IL to the next statement),
					// unless that piece's block has no effect and an earlier piece does: upstream also gives the type
					// test at the start of a filter ('isinst; dup; brtrue') to the try block's last statement.
					int homeBlock = flow.Block[pieces[pieces.Count - 1].start];
					if (!pieces.Any(p => flow.Block[p.start] == homeBlock && HasEffect(p.start, p.end))) {
						for (int p = pieces.Count - 1; p >= 0; p--) {
							if (HasEffect(pieces[p].start, pieces[p].end)) {
								homeBlock = flow.Block[pieces[p].start];
								break;
							}
						}
					}
					var kept = pieces.Where(p => flow.Block[p.start] == homeBlock).ToList();
					var withEffect = kept.Where(p => !IsJumpOnly(p.start, p.end)).ToList();
					if (withEffect.Count != 0)
						kept = withEffect;
					foreach (var (pStart, pEnd) in kept)
						result.Add((pStart, pEnd, text));
				}
				spans = result;
				RebuildOwners();
			}

			void SplitUntilStable() {
				// Dropping a piece can make a branch from it come from outside: repeat until nothing changes
				for (int i = 0; i < 8; i++) {
					var before = spans.ToArray();
					SplitAtEntries();
					if (before.SequenceEqual(spans))
						break;
				}
			}

			/// <summary>
			/// Splits a span at every instruction that can be entered without executing the previous instruction of
			/// the span: a branch target (from code that isn't part of the same statement) or the start of an exception
			/// handler or filter. (Entering a try block isn't one: it's reached by falling through, e.g. lock and using
			/// statements start before their try block.)
			/// </summary>
			List<(int start, int end)> Split(int start, int end, TextSpan text, TextSpan?[] textOf) {
				var pieces = new List<(int, int)>();
				int pieceStart = start;
				for (int i = start + 1; i < end; i++) {
					bool cut = flow.IsHandlerStart[i];
					if (!cut) {
						foreach (int source in flow.BranchSources(i)) {
							if (textOf[source] is not TextSpan sourceText || !IsSameFamily(sourceText, text)) {
								cut = true;
								break;
							}
						}
					}
					if (cut) {
						pieces.Add((pieceStart, i));
						pieceStart = i;
					}
				}
				pieces.Add((pieceStart, end));
				return pieces;
			}

			/// <summary>
			/// A piece of a statement that's only entered from another statement's code and that computes a value for it
			/// belongs to that statement, e.g. the arm of 'x = c ? a : b;' that upstream attributed to the statement
			/// after it (at the arms' join point): the breakpoint on the next statement fired only when that arm ran.
			/// The statement's last piece (its own code) is never moved, except a brace's code that calls something (a
			/// brace doesn't compute anything: e.g. the rest of an 'if' condition that upstream gave to the method's
			/// closing brace). (Not in a state machine, where the compiler maps code like SetResult() to the brace.)
			/// </summary>
			void ReassignPiecesOfOtherStatements() {
				bool changed = false;
				var lastPiece = new Dictionary<TextSpan, int>();
				for (int i = 0; i < spans.Count; i++) {
					if (!lastPiece.TryGetValue(spans[i].text, out int last) || spans[i].start > spans[last].start)
						lastPiece[spans[i].text] = i;
				}
				for (int si = 0; si < spans.Count; si++) {
					var (start, end, text) = spans[si];
					if (flow.IsHandlerStart[start])
						continue;
					if (IsBrace(text)) {
						if (!context.IsStateMachine && HasCall(start, end) && OnlyEnteredFrom(start, text) is int caller && !IsBrace(spans[caller].text)) {
							spans[si] = (start, end, spans[caller].text);
							changed = true;
						}
						continue;
					}
					if (lastPiece[text] == si)
						continue;
					if (OnlyEnteredFrom(start, text) is int source && IsPartOfExpression(start, end, spans[source].start, spans[source].text)) {
						spans[si] = (start, end, spans[source].text);
						changed = true;
					}
				}
				if (changed)
					MergeAdjacentPieces();
			}

			/// <summary>
			/// The statement whose code is the only way to reach <paramref name="start"/>, if any
			/// </summary>
			int? OnlyEnteredFrom(int start, TextSpan text) {
				int? from = null;
				foreach (int pred in Predecessors(start)) {
					int o = owner[pred];
					if (o < 0 || spans[o].text == text || (from is int f && spans[f].text != spans[o].text))
						return null;
					from = o;
				}
				return from;
			}

			/// <summary>
			/// Is the code part of the evaluation of a statement's expression: it uses values that the statement left
			/// on the stack (it's only reached from the statement's piece at <paramref name="pieceStart"/>; not values
			/// that optimized code keeps on the stack across statements), or it leaves values for the statement's code
			/// after it (e.g. the arm of a conditional expression)?
			/// </summary>
			bool IsPartOfExpression(int start, int end, int pieceStart, TextSpan statement) {
				if (flow.StackDepth[start] > 0 && flow.StackDepth[start] > flow.StackDepth[pieceStart])
					return true;
				int last = end - 1;
				if (last < start || flow.StackDepth[last] < 0)
					return false;
				flow.Instructions[last].CalculateStackUsage(flow.HasReturnValue, out int pushes, out int pops);
				if (pops == -1 || flow.StackDepth[last] - pops + pushes <= flow.StackDepth[start])
					return false;
				foreach (int next in flow.Successors(last)) {
					if (next >= count)
						continue;
					if (owner[next] >= 0 && spans[owner[next]].text == statement)
						return true;
					// The join of a conditional expression's arms (e.g. its store) that upstream gave to the next statement
					bool join = false;
					bool fromStatement = true;
					foreach (int pred in Predecessors(next)) {
						if (pred == last)
							continue;
						join = true;
						if (owner[pred] < 0 || spans[owner[pred]].text != statement)
							fromStatement = false;
					}
					if (join && fromStatement)
						return true;
				}
				return false;
			}

			/// <summary>
			/// The instructions of the method's epilogues without a statement: a 'ret' outside of exception handling
			/// blocks and the loads of its return value before it, entered at their start only and on an empty stack
			/// (else the 'ret' returns the value of a statement's expression, e.g. an arm of 'return a ? b : c;' in
			/// optimized code). None if the closing brace can't have them.
			/// </summary>
			bool[] GetEpilogue() {
				var epilogue = new bool[count];
				if (context.ClosingBrace is null || context.IsStateMachine)
					return epilogue;
				for (int i = 0; i < count; i++) {
					if (owner[i] >= 0 || flow.Instructions[i].OpCode.Code != Code.Ret || flow.Block[i] != -1)
						continue;
					int start = i;
					while (start > 0 && !flow.BranchSources(start).Any() && owner[start - 1] < 0 && IsEpilogueInstruction(flow.Instructions[start - 1]))
						start--;
					if (flow.StackDepth[start] != 0)
						continue;
					for (int j = start; j <= i; j++)
						epilogue[j] = true;
				}
				return epilogue;
			}

			/// <summary>
			/// The epilogue (load the return value, ret) is the closing brace. In an async method, it's where the state
			/// is set to 'finished' (-2) before the result is set (and not in the catch handler, the exception path).
			/// Only if there's one: the breakpoint on the brace binds to one place.
			/// </summary>
			void AddEpilogue() {
				if (context.ClosingBrace is not TextSpan closingBrace || spans.Any(a => a.text == closingBrace))
					return;
				var candidates = new List<(int start, int end)>();
				for (int i = 0; i < count; i++) {
					if (owner[i] >= 0)
						continue;
					if (context.IsStateMachine) {
						if (i + 2 < count && IsFinishedStateStore(i) && owner[i + 1] < 0 && owner[i + 2] < 0 && !flow.IsInHandler(i) &&
							flow.Block[i] == flow.Block[i + 2] && !flow.BranchSources(i + 1).Any() && !flow.BranchSources(i + 2).Any())
							candidates.Add((i, i + 3));
						continue;
					}
					if (flow.Instructions[i].OpCode.Code != Code.Ret || flow.Block[i] != -1)
						continue;
					int epilogueStart = i;
					while (epilogueStart > 0 && owner[epilogueStart - 1] < 0 && IsEpilogueInstruction(flow.Instructions[epilogueStart - 1]))
						epilogueStart--;
					// Only entered at its start (it's the return statements' common target)
					bool singleEntry = true;
					for (int j = epilogueStart + 1; j <= i; j++) {
						if (flow.BranchSources(j).Any())
							singleEntry = false;
					}
					if (singleEntry)
						candidates.Add((epilogueStart, i + 1));
				}
				if (candidates.Count != 1)
					return;
				spans.Add((candidates[0].start, candidates[0].end, closingBrace));
				RebuildOwners();
			}

			/// <summary>
			/// Conditions' tails attributed to the next statement: a simple statement can't start with a conditional
			/// branch to another statement's code
			/// </summary>
			void MoveConditionTails() {
				var pieceCount = CountPieces();
				// The first piece of each statement (a statement's own temporaries are stored before they're used)
				var firstStart = new Dictionary<TextSpan, int>();
				foreach (var span in spans) {
					if (!firstStart.TryGetValue(span.text, out int f) || span.start < f)
						firstStart[span.text] = span.start;
				}
				bool changed = false;
				for (int si = 0; si < spans.Count; si++) {
					var (start, end, text) = spans[si];
					var kind = KindOf(text);
					if (kind == StatementKind.Brace)
						continue;
					int cut = -1;
					// A statement doesn't start by using a compiler temporary it didn't store: it's the end of the previous
					// statement (e.g. the 'ToStringAndClear()' of an interpolated string's handler, the test of a condition
					// stored in a temporary)
					if (GetTemporaryUseEnd(start, end) is (int useEnd, int temp)) {
						// (Skipping nops without a statement)
						int gapStart = start;
						while (gapStart > 0 && owner[gapStart - 1] < 0 && flow.Instructions[gapStart - 1].OpCode.Code == Code.Nop && !flow.BranchSources(gapStart).Any())
							gapStart--;
						int prevStatement = gapStart > 0 ? owner[gapStart - 1] : -1;
						if (prevStatement >= 0 && spans[prevStatement].end == gapStart && !IsBrace(spans[prevStatement].text) && !flow.BranchSources(start).Any() &&
							flow.Block[gapStart - 1] == flow.Block[start] && UsesLocal(spans[prevStatement].start, gapStart, temp)) {
							spans[prevStatement] = (spans[prevStatement].start, useEnd, spans[prevStatement].text);
							spans[si] = (useEnd, end, text);
							for (int i = gapStart; i < useEnd; i++)
								owner[i] = prevStatement;
							changed = true;
							continue;
						}
					}
					// A test of a compiler temporary the statement didn't store is another statement's condition, e.g. optimized
					// code's 'if (n is null or "")' stores its result in a temporary in each arm and tests it, a state
					// machine's dispatch tests its state ('ldloc.0; ldc.i4.1; beq'), upstream gave that to the next statement
					bool testsTemporary = TestsTemporary(start, end) is int testEnd && !UsesLocal(firstStart[text], start, GetLocalIndex(flow.Instructions[start])!.Value);
					if (testsTemporary)
						cut = TestsTemporary(start, end)!.Value;
					else if (kind == StatementKind.Other)
						continue;
					for (int i = start; !testsTemporary && i < end; i++) {
						if (flow.Instructions[i].OpCode.FlowControl != FlowControl.Cond_Branch)
							continue;
						foreach (int target in flow.BranchTargets(i)) {
							// A branch to its end skips the rest of the statement ('x?.M()')
							bool inside = target >= start && (target < end || (target == end && kind == StatementKind.SimpleBranching));
							if (!inside && target < count && owner[target] >= 0 && !IsPartOf(spans[owner[target]].text, text))
								cut = i + 1;
						}
					}
					if (cut < 0)
						continue;
					// The statement's code after the condition doesn't use values from before it (it can use less: the
					// piece can start in the middle of the condition, e.g. at the join of a 'x?.ToString()' in it)
					if (cut < count && flow.StackDepth[cut] > flow.StackDepth[start])
						continue;
					// The whole piece is the condition: only if the statement has another piece (its own code)
					if (cut >= end) {
						if (pieceCount[text] < 2)
							continue;
						pieceCount[text]--;
						cut = end;
					}
					// The previous statement gets it if the condition is its own
					int prev = start > 0 ? owner[start - 1] : -1;
					bool toPrevious = prev >= 0 && spans[prev].end == start && KindOf(spans[prev].text) == StatementKind.Other && flow.Block[start - 1] == flow.Block[start];
					for (int i = start; i < cut; i++)
						owner[i] = toPrevious ? prev : -1;
					if (toPrevious)
						spans[prev] = (spans[prev].start, cut, spans[prev].text);
					spans[si] = (cut, end, text);
					changed = true;
				}
				if (changed)
					RebuildOwners();
			}

			/// <summary>
			/// If the code at <paramref name="start"/> uses a compiler temporary (ldloc/ldloca of a local the decompiled
			/// code doesn't show) until the stack is back where it was (optionally ending with a conditional branch),
			/// returns where that ends and the local
			/// </summary>
			(int end, int local)? GetTemporaryUseEnd(int start, int end) {
				var first = flow.Instructions[start];
				if (!(first.IsLdloc() || first.OpCode.Code == Code.Ldloca || first.OpCode.Code == Code.Ldloca_S) || GetLocalIndex(first) is not int local || !context.IsTemporary(local))
					return null;
				int startDepth = flow.StackDepth[start];
				for (int i = start; i < end; i++) {
					var instr = flow.Instructions[i];
					var flowControl = instr.OpCode.FlowControl;
					if (flowControl == FlowControl.Cond_Branch)
						return i + 1 < count && flow.StackDepth[i + 1] == startDepth ? (i + 1, local) : null;
					if (flowControl != FlowControl.Next && flowControl != FlowControl.Call)
						return null;
					if (i > start && flow.StackDepth[i] == startDepth)
						return (i, local);
				}
				return null;
			}

			/// <summary>
			/// 'ldloc temp; brtrue/brfalse' (a condition stored in a boolean temporary) or, in a state machine,
			/// 'ldloc state; ldc.i4 N; beq/bne' (its dispatch): returns its end
			/// </summary>
			int? TestsTemporary(int start, int end) {
				var first = flow.Instructions[start];
				if (!first.IsLdloc() || GetLocalIndex(first) is not int local || (uint)local >= (uint)flow.Locals.Count)
					return null;
				var type = flow.Locals[local].Type?.RemovePinnedAndModifiers().GetElementType();
				int i = start + 1;
				// (Optimized code reuses the slot of a state machine's flags for variables)
				if (type == ElementType.I4 && context.IsStateMachine && i < end && flow.Instructions[i].IsLdcI4() && context.IsTemporary(local, flow.Offsets[start]))
					i++;
				else if (type != ElementType.Boolean || !context.IsTemporary(local))
					return null;
				if (i < end && flow.Instructions[i].OpCode.FlowControl == FlowControl.Cond_Branch && i + 1 < count && flow.StackDepth[i + 1] == flow.StackDepth[start])
					return i + 1;
				return null;
			}

			/// <summary>
			/// Does the code store or take the address of the local?
			/// </summary>
			bool UsesLocal(int start, int end, int local) {
				for (int i = start; i < end; i++) {
					var instr = flow.Instructions[i];
					if ((instr.IsStloc() || instr.OpCode.Code == Code.Ldloca || instr.OpCode.Code == Code.Ldloca_S) && GetLocalIndex(instr) == local)
						return true;
				}
				return false;
			}

			/// <summary>
			/// The nop after a call that ends a statement (debug code), or the jump to the end of its block (e.g. the
			/// 'leave' of a catch block in optimized code), is where a step out of the called method returns: the
			/// compiler maps it to the statement, so the caller's frame shows the call's statement. Upstream gives it to
			/// no statement when the next statement doesn't start with it (e.g. one that uses a state machine's hoisted
			/// local), and the frame showed no statement at all.
			/// </summary>
			void GiveReturnAddressesToCalls() {
				for (int i = 1; i < count; i++) {
					if (owner[i] >= 0 || flow.Instructions[i].OpCode.Code is not (Code.Nop or Code.Br or Code.Br_S or Code.Leave or Code.Leave_S))
						continue;
					int si = owner[i - 1];
					if (si < 0 || spans[si].end != i || IsBrace(spans[si].text))
						continue;
					if (flow.Instructions[i - 1].OpCode.Code is not (Code.Call or Code.Callvirt))
						continue;
					// Only reached from the call, at the end of the statement's expression
					if (flow.BranchSources(i).Any() || flow.Block[i] != flow.Block[i - 1] || flow.StackDepth[i] != 0)
						continue;
					spans[si] = (spans[si].start, i + 1, spans[si].text);
					owner[i] = si;
				}
			}

			/// <summary>
			/// The runtime binds a breakpoint only where the JIT has a boundary: where the stack is empty, after a call,
			/// after a nop and at the start of a handler or filter (implicit boundaries without a PDB). A statement that starts elsewhere (e.g. after the
			/// previous await's GetResult() that upstream attributed to it, or with the store of a filter's exception
			/// variable) starts at its first boundary instead, if the code before it doesn't call anything (a frame in
			/// that call keeps its statement). An awaiter's GetResult() is hidden, like the compiler does.
			/// </summary>
			void StartAtBoundaries() {
				var first = new Dictionary<TextSpan, int>();
				for (int si = 0; si < spans.Count; si++) {
					if (!first.TryGetValue(spans[si].text, out int f) || spans[si].start < spans[f].start)
						first[spans[si].text] = si;
				}
				bool changed = false;
				foreach (int si in first.Values) {
					var (start, end, text) = spans[si];
					if (IsBoundary(start))
						continue;
					// The IL without a statement at the method's start that pushes the values it starts with (the 'this'
					// of a field initializer's store). (Elsewhere, the compiler also hides such IL, e.g. the load of
					// a switch expression's value that's returned.)
					int back = start;
					while (back > 0 && start - back < 8 && owner[back - 1] < 0 && flow.Block[back - 1] == flow.Block[start] &&
						MethodFlow.FallsThrough(flow.Instructions[back - 1]) && !flow.IsHandlerStart[back] && !flow.BranchSources(back).Any() &&
						flow.StackDepth[back - 1] >= 0 && !HasEffect(back - 1, back)) {
						back--;
						if (IsBoundary(back))
							break;
					}
					if (back == 0 && back < start) {
						spans[si] = (back, end, text);
						changed = true;
						continue;
					}
					int boundary = start + 1;
					while (boundary < end && !IsBoundary(boundary))
						boundary++;
					if (boundary >= end)
						continue;
					bool calls = false;
					for (int i = start; i < boundary; i++) {
						var instr = flow.Instructions[i];
						if (instr.OpCode.FlowControl == FlowControl.Call && !(instr.Operand is IMethod called && called.Name == "GetResult"))
							calls = true;
					}
					if (calls)
						continue;
					spans[si] = (boundary, end, text);
					changed = true;
				}
				if (changed)
					RebuildOwners();
			}

			bool IsBoundary(int index) {
				if (index == 0 || flow.StackDepth[index] == 0 || flow.IsHandlerStart[index])
					return true;
				if (flow.StackDepth[index] < 0 || index >= count)
					return false;
				switch (flow.Instructions[index - 1].OpCode.Code) {
				case Code.Call:
				case Code.Callvirt:
				case Code.Calli:
				case Code.Newobj:
				case Code.Newarr:
				case Code.Nop:
					return true;
				}
				return false;
			}

			/// <summary>
			/// dnSpy's breakpoint is on the first instruction of a statement's lowest IL span: that piece must be the
			/// statement's entry. The entry is the piece that dominates the most other pieces (ties: the last one,
			/// upstream attributes leftover IL to the next statement). Pieces before it
			/// or that it doesn't dominate are other statements' code: they go to the statement they're only reached
			/// from, else they're hidden.
			/// </summary>
			void KeepEntries() {
				var byText = new Dictionary<TextSpan, List<int>>();
				for (int si = 0; si < spans.Count; si++) {
					if (IsBrace(spans[si].text))
						continue;
					if (!byText.TryGetValue(spans[si].text, out var list))
						byText.Add(spans[si].text, list = new List<int>());
					list.Add(si);
				}
				var dropped = new List<int>();
				foreach (var pieces in byText.Values) {
					if (pieces.Count < 2)
						continue;
					int entry = -1;
					(int hasCode, int dominated, int start) best = (-1, -1, -1);
					foreach (int p in pieces) {
						// (A piece that only jumps, e.g. a filter's endfilter, isn't the statement's code)
						int hasCode = IsJumpOnly(spans[p].start, spans[p].end) ? 0 : 1;
						int dominated = pieces.Count(q => q != p && flow.Dominates(spans[p].start, spans[q].start));
						var key = (hasCode, dominated, spans[p].start);
						if (key.CompareTo(best) > 0) {
							best = key;
							entry = p;
						}
					}
					foreach (int p in pieces) {
						if (p != entry && (spans[p].start < spans[entry].start || !flow.Dominates(spans[entry].start, spans[p].start)))
							dropped.Add(p);
					}
				}
				if (dropped.Count == 0)
					return;
				foreach (int p in dropped) {
					var (start, end, text) = spans[p];
					var to = OnlyEnteredFrom(start, text) is int from && !IsBrace(spans[from].text) ? spans[from].text : (TextSpan?)null;
					spans[p] = to is TextSpan t ? (start, end, t) : (start, start, text);
				}
				MergeAdjacentPieces();
			}

			/// <summary>
			/// Does the code at <paramref name="start"/> (until it branches or the stack is back to where it was) pop a
			/// value that was on the stack before it started?
			/// </summary>
			bool ConsumesStack(int start, int end) {
				int startDepth = flow.StackDepth[start];
				if (startDepth <= 0)
					return false;
				for (int i = start; i < end && i < count; i++) {
					int depth = flow.StackDepth[i];
					if (depth < 0)
						return false;
					var instr = flow.Instructions[i];
					instr.CalculateStackUsage(flow.HasReturnValue, out _, out int pops);
					if (pops == -1 || depth - pops < startDepth)
						return true;
					switch (instr.OpCode.FlowControl) {
					case FlowControl.Branch:
					case FlowControl.Cond_Branch:
					case FlowControl.Return:
					case FlowControl.Throw:
						return false;
					}
				}
				return false;
			}

			/// <summary>
			/// If the stack-empty run of unattributed IL at <paramref name="runStart"/> reads a compiler temporary that
			/// the statement stored and doesn't branch, returns its end, else <paramref name="runStart"/>
			/// </summary>
			int GetTemporaryRunEnd(int statementStart, int runStart) {
				var stored = new HashSet<int>();
				for (int i = statementStart; i < runStart; i++) {
					var instr = flow.Instructions[i];
					// (Stored, or initialized through its address, e.g. an interpolated string's handler)
					if ((instr.IsStloc() || instr.OpCode.Code == Code.Ldloca || instr.OpCode.Code == Code.Ldloca_S) && GetLocalIndex(instr) is int index && context.IsTemporary(index))
						stored.Add(index);
				}
				if (stored.Count == 0)
					return runStart;
				bool readsTemporary = false;
				bool calls = false;
				int end = runStart;
				do {
					if (owner[end] >= 0 || flow.Block[end] != flow.Block[runStart] || flow.StackDepth[end] < flow.StackDepth[runStart])
						return runStart;
					var instr = flow.Instructions[end];
					// (It can end with a conditional branch: the test of a condition the statement stored. Not of a call's
					// result, e.g. an awaiter's IsCompleted: the compiler hides it.)
					bool test = instr.OpCode.FlowControl == FlowControl.Cond_Branch && readsTemporary && !calls;
					if (instr.OpCode.FlowControl == FlowControl.Call)
						calls = true;
					if (instr.OpCode.FlowControl != FlowControl.Next && instr.OpCode.FlowControl != FlowControl.Call && !test)
						return runStart;
					if ((instr.IsLdloc() || instr.OpCode.Code == Code.Ldloca || instr.OpCode.Code == Code.Ldloca_S) && GetLocalIndex(instr) is int index && stored.Contains(index))
						readsTemporary = true;
					end++;
					if (test)
						break;
				} while (end < count && flow.StackDepth[end] != flow.StackDepth[runStart]);
				return readsTemporary && (end >= count || flow.StackDepth[end] == flow.StackDepth[runStart] || !MethodFlow.FallsThrough(flow.Instructions[end - 1])) ? end : runStart;
			}

			/// <summary>
			/// ldarg.0; ldc.i4.s -2; stfld &lt;&gt;1__state (the C# compiler's 'finished' state)
			/// </summary>
			bool IsFinishedStateStore(int i) {
				var instrs = flow.Instructions;
				return instrs[i].OpCode.Code == Code.Ldarg_0 &&
					instrs[i + 1].IsLdcI4() && instrs[i + 1].GetLdcI4Value() == -2 &&
					instrs[i + 2].OpCode.Code == Code.Stfld && instrs[i + 2].Operand is IField field && field.Name == "<>1__state";
			}

			bool HasCall(int start, int end) {
				for (int i = start; i < end; i++) {
					if (flow.Instructions[i].OpCode.FlowControl == FlowControl.Call)
						return true;
				}
				return false;
			}

			/// <summary>
			/// Does the code call a method, store something, throw or return?
			/// </summary>
			bool HasEffect(int start, int end) {
				for (int i = start; i < end; i++) {
					var opCode = flow.Instructions[i].OpCode;
					switch (opCode.FlowControl) {
					case FlowControl.Call:
					case FlowControl.Throw:
						return true;
					}
					switch (opCode.Code) {
					case Code.Ret:
					case Code.Stloc: case Code.Stloc_S: case Code.Stloc_0: case Code.Stloc_1: case Code.Stloc_2: case Code.Stloc_3:
					case Code.Starg: case Code.Starg_S: case Code.Stfld: case Code.Stsfld: case Code.Stobj: case Code.Initobj:
					case Code.Cpobj: case Code.Cpblk: case Code.Initblk:
					case Code.Stind_I: case Code.Stind_I1: case Code.Stind_I2: case Code.Stind_I4: case Code.Stind_I8:
					case Code.Stind_R4: case Code.Stind_R8: case Code.Stind_Ref:
					case Code.Stelem: case Code.Stelem_I: case Code.Stelem_I1: case Code.Stelem_I2: case Code.Stelem_I4:
					case Code.Stelem_I8: case Code.Stelem_R4: case Code.Stelem_R8: case Code.Stelem_Ref:
						return true;
					}
				}
				return false;
			}

			bool IsJumpOnly(int start, int end) {
				for (int i = start; i < end; i++) {
					if (!IsJump(flow.Instructions[i]))
						return false;
				}
				return true;
			}
		}

		static int? GetLocalIndex(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Ldloc_0: case Code.Stloc_0: return 0;
			case Code.Ldloc_1: case Code.Stloc_1: return 1;
			case Code.Ldloc_2: case Code.Stloc_2: return 2;
			case Code.Ldloc_3: case Code.Stloc_3: return 3;
			}
			return (instr.Operand as Local)?.Index;
		}

		static bool IsEpilogueInstruction(Instruction instr) =>
			instr.OpCode.Code == Code.Nop || instr.IsLdloc() || instr.IsLdarg();

		/// <summary>
		/// Instructions that only transfer control (a filter's endfilter and a finally's endfinally are reached from
		/// every path, e.g. a filter's type check failing)
		/// </summary>
		static bool IsJump(Instruction instr) {
			switch (instr.OpCode.Code) {
			case Code.Nop:
			case Code.Br:
			case Code.Br_S:
			case Code.Leave:
			case Code.Leave_S:
			case Code.Endfilter:
			case Code.Endfinally:
				return true;
			default:
				return false;
			}
		}
	}
}
