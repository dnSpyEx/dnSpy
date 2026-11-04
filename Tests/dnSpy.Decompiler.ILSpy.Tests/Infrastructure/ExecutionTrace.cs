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
using System.Runtime.Loader;
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// One executed IL instruction: method token and IL offset in the original (uninstrumented) module
	/// </summary>
	readonly struct TraceEvent {
		public readonly int Thread;
		public readonly uint Token;
		public readonly uint Offset;
		public readonly int Depth;

		public TraceEvent(int thread, uint token, uint offset, int depth) {
			Thread = thread;
			Token = token;
			Offset = offset;
			Depth = depth;
		}

		public override string ToString() => $"0x{Token:X8} IL_{Offset:X4} (thread {Thread}, depth {Depth})";
	}

	/// <summary>
	/// One invocation of a method: its events, in execution order
	/// </summary>
	sealed class TraceFrame {
		public int Id { get; }
		public uint Token { get; }
		public List<int> Events { get; } = new List<int>();
		public TraceFrame(int id, uint token) {
			Id = id;
			Token = token;
		}
		public override string ToString() => $"frame {Id} 0x{Token:X8} ({Events.Count} events)";
	}

	/// <summary>
	/// Records the IL instructions a program executes. The program's IL is rewritten so that each instruction is
	/// preceded by a call to <see cref="Hit"/> with the instruction's original method token and IL offset.
	/// </summary>
	public static class ExecutionTrace {
		const int MaxEvents = 2_000_000;
		static readonly object lockObj = new object();
		static List<TraceEvent>? events;

		/// <summary>
		/// Called by the instrumented code before each instruction
		/// </summary>
		public static void Hit(int token, int offset) {
			var list = events;
			if (list is null)
				return;
			// Instrumented methods aren't inlined, so the number of frames is the call depth
			int depth = new StackTrace(false).FrameCount;
			lock (lockObj) {
				// Too much: the program probably doesn't terminate
				if (list.Count >= MaxEvents)
					throw new InvalidOperationException($"More than {MaxEvents} instructions executed");
				list.Add(new TraceEvent(Environment.CurrentManagedThreadId, (uint)token, (uint)offset, depth));
			}
		}

		/// <summary>
		/// Instruments a module, runs <paramref name="entryPoint"/> (a static method without parameters) and returns
		/// what it executed
		/// </summary>
		internal static List<TraceEvent> Run(byte[] image, string entryType, string entryMethod) {
			var instrumented = Instrument(image);
			var context = new AssemblyLoadContext("trace", isCollectible: true);
			List<TraceEvent> result;
			try {
				var assembly = context.LoadFromStream(new MemoryStream(instrumented));
				var method = assembly.GetType(entryType, throwOnError: true)!.GetMethod(entryMethod)!;
				lock (lockObj) {
					if (events is not null)
						throw new InvalidOperationException("Only one trace at a time");
					events = new List<TraceEvent>();
				}
				try {
					// On another thread: a test program that doesn't terminate fails the test instead of hanging it
					Exception? error = null;
					var thread = new Thread(() => {
						try {
							// async Task Main()
							if (method.Invoke(null, null) is System.Threading.Tasks.Task task)
								task.GetAwaiter().GetResult();
						}
						catch (Exception ex) {
							error = ex;
						}
					}) { IsBackground = true };
					thread.Start();
					if (!thread.Join(TimeSpan.FromSeconds(60)))
						throw new TimeoutException("The traced program didn't finish within 60 seconds");
					if (error is not null)
						throw new InvalidOperationException("The traced program failed", error);
					// Async continuations may still run on other threads
					Thread.Sleep(200);
				}
				finally {
					lock (lockObj) {
						result = events!;
						events = null;
					}
				}
			}
			finally {
				context.Unload();
			}
			return result;
		}

		internal static byte[] Instrument(byte[] image) {
			var module = ModuleDefMD.Load(image, TestCompiler.CreateModuleContext());
			var hit = module.Import(typeof(ExecutionTrace).GetMethod(nameof(Hit))!);
			foreach (var type in module.GetTypes()) {
				foreach (var method in type.Methods) {
					if (!method.HasBody || method.Body.Instructions.Count == 0)
						continue;
					var body = method.Body;
					var original = body.Instructions.ToList();
					// (Computed first: the instructions are modified below)
					var afterPrefix = original.Select((a, i) => i > 0 && original[i - 1].OpCode.OpCodeType == OpCodeType.Prefix).ToArray();
					// Set when the body was read: the offsets in the original module
					var offsets = original.ToDictionary(a => a, a => a.Offset);
					body.SimplifyMacros(method.Parameters);
					body.SimplifyBranches();
					var list = new List<Instruction>(original.Count * 4);
					for (int i = 0; i < original.Count; i++) {
						var instr = original[i];
						// Nothing can go between a prefix and its instruction
						if (afterPrefix[i]) {
							list.Add(instr);
							continue;
						}
						// The original instruction object becomes the probe's first instruction, so branches and
						// exception handlers that reference it now start at the probe
						var copy = new Instruction(instr.OpCode, instr.Operand);
						instr.OpCode = OpCodes.Ldc_I4;
						instr.Operand = (int)method.MDToken.Raw;
						list.Add(instr);
						list.Add(Instruction.Create(OpCodes.Ldc_I4, (int)offsets[instr]));
						list.Add(Instruction.Create(OpCodes.Call, hit));
						list.Add(copy);
					}
					body.Instructions.Clear();
					foreach (var instr in list)
						body.Instructions.Add(instr);
					body.KeepOldMaxStack = false;
					method.ImplAttributes |= MethodImplAttributes.NoInlining | MethodImplAttributes.NoOptimization;
				}
			}
			var stream = new MemoryStream();
			module.Write(stream);
			return stream.ToArray();
		}

		/// <summary>
		/// Splits events into method invocations (frames). A new frame starts when a method is called (deeper),
		/// when another method runs at the same depth, or when a method starts over at offset 0 (e.g. the next
		/// MoveNext() of a state machine), unless offset 0 is a branch target.
		/// </summary>
		internal static List<TraceFrame> GetFrames(List<TraceEvent> events, ISet<uint> methodsLoopingToStart) {
			var frames = new List<TraceFrame>();
			var stacks = new Dictionary<int, List<TraceFrame>>();
			var depths = new Dictionary<TraceFrame, int>();
			for (int i = 0; i < events.Count; i++) {
				var e = events[i];
				if (!stacks.TryGetValue(e.Thread, out var stack))
					stacks.Add(e.Thread, stack = new List<TraceFrame>());
				while (stack.Count != 0 && depths[stack[stack.Count - 1]] > e.Depth)
					stack.RemoveAt(stack.Count - 1);
				if (stack.Count != 0 && depths[stack[stack.Count - 1]] == e.Depth) {
					var top = stack[stack.Count - 1];
					if (top.Token == e.Token && (e.Offset != 0 || methodsLoopingToStart.Contains(e.Token))) {
						top.Events.Add(i);
						continue;
					}
					stack.RemoveAt(stack.Count - 1);
				}
				var frame = new TraceFrame(frames.Count, e.Token);
				frame.Events.Add(i);
				frames.Add(frame);
				depths.Add(frame, e.Depth);
				stack.Add(frame);
			}
			return frames;
		}
	}
}
