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
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	/// <summary>
	/// Instruction offsets, branch sources, evaluation stack depths and exception handling blocks of a method body.
	/// Computed without modifying the body (it's shared with the UI).
	/// </summary>
	sealed class MethodFlow {
		public IList<Instruction> Instructions { get; }
		public int Count => Instructions.Count;
		/// <summary>Offset of each instruction, plus the code size at index <see cref="Count"/></summary>
		public uint[] Offsets { get; }
		/// <summary>Stack depth before each instruction, -1 if it's unreachable</summary>
		public int[] StackDepth { get; }
		/// <summary>Innermost exception handling block (try, filter or handler) of each instruction, -1 if none</summary>
		public int[] Block { get; }
		public bool[] IsHandlerStart { get; }
		public bool HasReturnValue { get; }
		public IList<Local> Locals { get; }

		/// <summary>true if the instruction is in a catch/finally/fault handler or filter (its innermost block)</summary>
		public bool IsInHandler(int index) => Block[index] >= 0 && Block[index] % 3 != 0;
		readonly List<int>?[] branchSources;
		readonly Dictionary<uint, int> indexes;
		readonly Dictionary<Instruction, int> indexOf;
		/// <summary>Handlers and filters entered from an instruction (a try block's start, a filter's start)</summary>
		readonly Dictionary<int, List<int>> handlerEdges;
		Dominators? dominators;

		MethodFlow(IList<Instruction> instructions, uint[] offsets, int[] stackDepth, int[] block, bool[] isHandlerStart, List<int>?[] branchSources, Dictionary<uint, int> indexes, Dictionary<Instruction, int> indexOf, bool hasReturnValue, Dictionary<int, List<int>> handlerEdges, IList<Local> locals) {
			this.handlerEdges = handlerEdges;
			Locals = locals;
			HasReturnValue = hasReturnValue;
			Instructions = instructions;
			Offsets = offsets;
			StackDepth = stackDepth;
			Block = block;
			IsHandlerStart = isHandlerStart;
			this.branchSources = branchSources;
			this.indexes = indexes;
			this.indexOf = indexOf;
		}

		public bool TryGetIndex(uint offset, out int index) => indexes.TryGetValue(offset, out index);

		/// <summary>
		/// Does every execution path from the method's start to instruction <paramref name="b"/> go through
		/// instruction <paramref name="a"/>? (An unreachable instruction is dominated by everything: it never runs.)
		/// </summary>
		public bool Dominates(int a, int b) => (dominators ??= Dominators.Compute(this)).Dominates(a, b);

		/// <summary>
		/// Instructions that can run right after an instruction: the next one (unless it doesn't fall through), its
		/// branch targets, and the handlers and filters of a try block (from its first instruction: an exception
		/// can only occur after the try block was entered)
		/// </summary>
		public IEnumerable<int> Successors(int index) {
			var instr = Instructions[index];
			if (FallsThrough(instr) && index + 1 < Count)
				yield return index + 1;
			foreach (int target in BranchTargets(index))
				yield return target;
			if (handlerEdges.TryGetValue(index, out var handlers)) {
				foreach (int handler in handlers)
					yield return handler;
			}
		}

		public static bool FallsThrough(Instruction instr) {
			switch (instr.OpCode.FlowControl) {
			case FlowControl.Branch:
			case FlowControl.Return:
			case FlowControl.Throw:
				return false;
			}
			return instr.OpCode.Code != Code.Endfinally && instr.OpCode.Code != Code.Endfilter && instr.OpCode.Code != Code.Jmp;
		}

		public ILSpan ToILSpan(int start, int end) => ILSpan.FromBounds(Offsets[start], Offsets[end]);

		/// <summary>Instructions that branch to an instruction (not counting fall through)</summary>
		public IEnumerable<int> BranchSources(int index) => (IEnumerable<int>?)branchSources[index] ?? Array.Empty<int>();

		/// <summary>Instructions a branch instruction branches to (not counting fall through)</summary>
		public IEnumerable<int> BranchTargets(int index) {
			switch (Instructions[index].Operand) {
			case Instruction target:
				if (indexOf.TryGetValue(target, out int t))
					yield return t;
				break;
			case IList<Instruction> targets:
				foreach (var target2 in targets) {
					if (target2 is not null && indexOf.TryGetValue(target2, out int t2))
						yield return t2;
				}
				break;
			}
		}

		public static MethodFlow? TryCreate(MethodDef method) {
			var body = method.Body;
			var instrs = body.Instructions;
			int count = instrs.Count;
			var offsets = new uint[count + 1];
			var indexOf = new Dictionary<Instruction, int>(count);
			var indexes = new Dictionary<uint, int>(count + 1);
			uint offset = 0;
			for (int i = 0; i < count; i++) {
				var instr = instrs[i];
				if (instr is null || indexOf.ContainsKey(instr))
					return null;
				indexOf.Add(instr, i);
				offsets[i] = offset;
				indexes[offset] = i;
				offset += (uint)instr.GetSize();
			}
			offsets[count] = offset;
			indexes[offset] = count;

			var branchSources = new List<int>?[count + 1];
			for (int i = 0; i < count; i++) {
				switch (instrs[i].Operand) {
				case Instruction target:
					if (!indexOf.TryGetValue(target, out int t))
						return null;
					(branchSources[t] ??= new List<int>()).Add(i);
					break;
				case IList<Instruction> targets:
					foreach (var target2 in targets) {
						if (target2 is null || !indexOf.TryGetValue(target2, out int t2))
							return null;
						(branchSources[t2] ??= new List<int>()).Add(i);
					}
					break;
				}
			}

			int IndexOrEnd(Instruction? instr) => instr is null ? count : indexOf.TryGetValue(instr, out int index) ? index : -1;
			var block = new int[count];
			var blockSize = new int[count];
			for (int i = 0; i < count; i++) {
				block[i] = -1;
				blockSize[i] = int.MaxValue;
			}
			var isHandlerStart = new bool[count + 1];
			var handlerEdges = new Dictionary<int, List<int>>();
			void AddHandlerEdge(int from, int to) {
				if (from < count && to < count) {
					if (!handlerEdges.TryGetValue(from, out var list))
						handlerEdges.Add(from, list = new List<int>());
					list.Add(to);
				}
			}
			var handlers = body.ExceptionHandlers;
			for (int h = 0; h < handlers.Count; h++) {
				var eh = handlers[h];
				int tryStart = IndexOrEnd(eh.TryStart), tryEnd = IndexOrEnd(eh.TryEnd);
				int handlerStart = IndexOrEnd(eh.HandlerStart), handlerEnd = IndexOrEnd(eh.HandlerEnd);
				if (tryStart < 0 || tryEnd < 0 || handlerStart < 0 || handlerEnd < 0 || eh.TryStart is null || eh.HandlerStart is null)
					return null;
				SetBlock(block, blockSize, tryStart, tryEnd, h * 3);
				SetBlock(block, blockSize, handlerStart, handlerEnd, h * 3 + 1);
				isHandlerStart[handlerStart] = true;
				AddHandlerEdge(tryStart, handlerStart);
				if (eh.HandlerType == ExceptionHandlerType.Filter) {
					int filterStart = IndexOrEnd(eh.FilterStart);
					if (filterStart < 0 || eh.FilterStart is null)
						return null;
					// The filter ends where the handler starts
					SetBlock(block, blockSize, filterStart, handlerStart, h * 3 + 2);
					isHandlerStart[filterStart] = true;
					AddHandlerEdge(tryStart, filterStart);
					// The handler runs after its filter
					AddHandlerEdge(filterStart, handlerStart);
				}
			}

			var depth = CalculateStackDepths(method, instrs, indexOf, handlers);
			if (depth is null)
				return null;
			bool hasReturnValue = method.MethodSig?.RetType?.RemovePinnedAndModifiers().GetElementType() is ElementType type && type != ElementType.Void;
			return new MethodFlow(instrs, offsets, depth, block, isHandlerStart, branchSources, indexes, indexOf, hasReturnValue, handlerEdges, body.Variables);
		}

		static void SetBlock(int[] block, int[] blockSize, int start, int end, int id) {
			int size = end - start;
			for (int i = start; i < end && i < block.Length; i++) {
				// Innermost = smallest block
				if (size < blockSize[i]) {
					block[i] = id;
					blockSize[i] = size;
				}
			}
		}

		static int[]? CalculateStackDepths(MethodDef method, IList<Instruction> instrs, Dictionary<Instruction, int> indexOf, IList<ExceptionHandler> handlers) {
			int count = instrs.Count;
			var depth = new int[count];
			for (int i = 0; i < count; i++)
				depth[i] = -1;
			bool hasReturnValue = method.MethodSig?.RetType?.RemovePinnedAndModifiers().GetElementType() is ElementType type && type != ElementType.Void;
			var work = new Stack<(int index, int depth)>();
			work.Push((0, 0));
			foreach (var eh in handlers) {
				if (eh.HandlerStart is not null && indexOf.TryGetValue(eh.HandlerStart, out int h))
					work.Push((h, eh.HandlerType == ExceptionHandlerType.Catch || eh.HandlerType == ExceptionHandlerType.Filter ? 1 : 0));
				if (eh.FilterStart is not null && indexOf.TryGetValue(eh.FilterStart, out int f))
					work.Push((f, 1));
			}
			while (work.Count != 0) {
				var (index, d) = work.Pop();
				if (index >= count)
					continue;
				if (depth[index] >= 0) {
					// Valid IL has one stack depth per instruction
					if (depth[index] != d)
						return null;
					continue;
				}
				depth[index] = d;
				var instr = instrs[index];
				instr.CalculateStackUsage(hasReturnValue, out int pushes, out int pops);
				int after = pops == -1 ? 0 : d - pops + pushes;
				if (after < 0)
					return null;
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					if (instr.Operand is Instruction target && indexOf.TryGetValue(target, out int t))
						work.Push((t, instr.OpCode.Code == Code.Leave || instr.OpCode.Code == Code.Leave_S ? 0 : after));
					continue;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction condTarget && indexOf.TryGetValue(condTarget, out int ct))
						work.Push((ct, after));
					else if (instr.Operand is IList<Instruction> targets) {
						foreach (var t2 in targets) {
							if (indexOf.TryGetValue(t2, out int ti))
								work.Push((ti, after));
						}
					}
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					continue;
				}
				// endfinally/endfilter don't fall through
				if (instr.OpCode.Code == Code.Endfinally || instr.OpCode.Code == Code.Endfilter)
					continue;
				work.Push((index + 1, after));
			}
			return depth;
		}
	}
	/// <summary>
	/// Dominator tree of a method's instructions (Cooper, Harvey, Kennedy: "A Simple, Fast Dominance Algorithm")
	/// </summary>
	sealed class Dominators {
		// Position of each instruction in a depth first walk of the dominator tree, -1 if it's unreachable
		readonly int[] pre;
		readonly int[] post;

		Dominators(int[] pre, int[] post) {
			this.pre = pre;
			this.post = post;
		}

		public bool Dominates(int a, int b) {
			if (pre[b] < 0)
				return true;
			if (pre[a] < 0)
				return false;
			return pre[a] <= pre[b] && post[b] <= post[a];
		}

		public static Dominators Compute(MethodFlow flow) {
			int count = flow.Count;
			int root = count;
			var successors = new int[count + 1][];
			successors[root] = new[] { 0 };
			for (int i = 0; i < count; i++)
				successors[i] = ToArray(flow.Successors(i));

			// Reverse postorder from the root (iterative: methods can have 100,000 instructions)
			var rpoIndex = new int[count + 1];
			for (int i = 0; i <= count; i++)
				rpoIndex[i] = -1;
			var visited = new bool[count + 1];
			var postorder = new List<int>(count + 1);
			var stack = new Stack<(int node, int next)>();
			stack.Push((root, 0));
			visited[root] = true;
			while (stack.Count != 0) {
				var (node, next) = stack.Pop();
				var succ = successors[node];
				if (next < succ.Length) {
					stack.Push((node, next + 1));
					int s = succ[next];
					if (s >= 0 && s < count && !visited[s]) {
						visited[s] = true;
						stack.Push((s, 0));
					}
				}
				else
					postorder.Add(node);
			}
			var rpo = new int[postorder.Count];
			for (int i = 0; i < rpo.Length; i++) {
				rpo[i] = postorder[postorder.Count - 1 - i];
				rpoIndex[rpo[i]] = i;
			}

			var predecessors = new List<int>?[count + 1];
			foreach (int node in rpo) {
				foreach (int s in successors[node]) {
					if (s >= 0 && s < count)
						(predecessors[s] ??= new List<int>()).Add(node);
				}
			}

			var idom = new int[count + 1];
			for (int i = 0; i <= count; i++)
				idom[i] = -1;
			idom[root] = root;
			bool changed = true;
			while (changed) {
				changed = false;
				for (int i = 1; i < rpo.Length; i++) {
					int node = rpo[i];
					int newIdom = -1;
					foreach (int p in (IEnumerable<int>?)predecessors[node] ?? Array.Empty<int>()) {
						if (idom[p] < 0)
							continue;
						newIdom = newIdom < 0 ? p : Intersect(p, newIdom, idom, rpoIndex);
					}
					if (newIdom >= 0 && idom[node] != newIdom) {
						idom[node] = newIdom;
						changed = true;
					}
				}
			}

			// Number the dominator tree depth first
			var children = new List<int>?[count + 1];
			foreach (int node in rpo) {
				if (node != root && idom[node] >= 0)
					(children[idom[node]] ??= new List<int>()).Add(node);
			}
			var pre = new int[count + 1];
			var post = new int[count + 1];
			for (int i = 0; i <= count; i++)
				pre[i] = post[i] = -1;
			int counter = 0;
			var walk = new Stack<(int node, int next)>();
			walk.Push((root, 0));
			pre[root] = counter++;
			while (walk.Count != 0) {
				var (node, next) = walk.Pop();
				var list = children[node];
				if (list is not null && next < list.Count) {
					walk.Push((node, next + 1));
					int child = list[next];
					pre[child] = counter++;
					walk.Push((child, 0));
				}
				else
					post[node] = counter++;
			}
			return new Dominators(pre, post);
		}

		static int Intersect(int a, int b, int[] idom, int[] rpoIndex) {
			while (a != b) {
				while (rpoIndex[a] > rpoIndex[b])
					a = idom[a];
				while (rpoIndex[b] > rpoIndex[a])
					b = idom[b];
			}
			return a;
		}

		static int[] ToArray(IEnumerable<int> values) {
			var list = new List<int>(values);
			return list.ToArray();
		}
	}
}
