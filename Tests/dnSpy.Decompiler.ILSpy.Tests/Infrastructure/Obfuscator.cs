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
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	[Flags]
	enum Obfuscations {
		None			= 0,
		/// <summary>Names: unprintable, invisible, right-to-left, C# keywords, invalid identifiers, a type's name</summary>
		Rename			= 0x01,
		/// <summary>Each string literal is decrypted by a call at runtime</summary>
		Strings			= 0x02,
		/// <summary>Calls of static methods go through proxy methods</summary>
		ProxyCalls		= 0x04,
		/// <summary>Methods' blocks are dispatched by a state variable and a switch (control flow flattening)</summary>
		ControlFlow		= 0x08,
		/// <summary>Opaque predicates (dead but valid code) and unreachable invalid IL (stack underflow, type errors)</summary>
		JunkCode		= 0x10,
		/// <summary>Unused types: deeply nested, huge methods, 300 parameters, 40 generic parameters, obfuscator attributes</summary>
		JunkTypes		= 0x20,
		All				= Rename | Strings | ProxyCalls | ControlFlow | JunkCode | JunkTypes,
	}

	/// <summary>
	/// What real obfuscators (ConfuserEx, Dotfuscator, .NET Reactor, ...) do to an assembly, done with dnlib so the
	/// tests don't need protected samples: the obfuscated program still runs and gives the same result (except names
	/// it prints, with <see cref="Obfuscations.Rename"/>). The same seed gives the same assembly.
	/// </summary>
	sealed class Obfuscator {
		readonly ModuleDefMD module;
		readonly Random random;
		readonly Obfuscations what;
		readonly Func<IMemberDef, bool> keepName;
		readonly HashSet<string> literals = new HashSet<string>(StringComparer.Ordinal);
		readonly HashSet<MethodDef> added = new HashSet<MethodDef>();
		int nameCounter;

		Obfuscator(byte[] image, Obfuscations what, int seed, Func<IMemberDef, bool>? keepName) {
			// (With a resolver: references to members of generic instances resolve to our definitions)
			module = ModuleDefMD.Load(image, ModuleDef.CreateModuleContext());
			random = new Random(seed);
			this.what = what;
			this.keepName = keepName ?? (_ => false);
		}

		/// <param name="keepName">Members whose names must stay (e.g. the method a test calls by name)</param>
		public static byte[] Obfuscate(byte[] image, Obfuscations what, int seed = 1, Func<IMemberDef, bool>? keepName = null) =>
			new Obfuscator(image, what, seed, keepName).Run();

		byte[] Run() {
			// Like Obfuscar: names in string literals are used by reflection (GetMethod("X"), nameof(X))
			foreach (var method in AllMethods()) {
				foreach (var instr in method.Body.Instructions) {
					if (instr.Operand is string s)
						literals.Add(s);
				}
			}
			var memberRefs = CollectMemberRefsToOurDefs();

			if ((what & Obfuscations.JunkTypes) != 0)
				AddJunkTypes();
			if ((what & Obfuscations.Strings) != 0)
				EncryptStrings();
			if ((what & Obfuscations.ProxyCalls) != 0)
				AddProxyCalls();
			if ((what & Obfuscations.ControlFlow) != 0) {
				foreach (var method in AllMethods().ToList())
					Flatten(method);
			}
			if ((what & Obfuscations.JunkCode) != 0) {
				opaqueField = AddOpaqueField();
				foreach (var method in AllMethods().ToList()) {
					AddOpaquePredicate(method, opaqueField);
					AddUnreachableJunk(method);
				}
			}
			if ((what & Obfuscations.Rename) != 0)
				Rename(memberRefs);

			var options = new ModuleWriterOptions(module) {
				// Unreachable invalid IL has no valid max stack: keep the one set below
				Logger = DummyLogger.NoThrowInstance,
			};
			options.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
			foreach (var method in AllMethods())
				method.Body.MaxStack = Math.Max(method.Body.MaxStack, (ushort)64);
			var ms = new MemoryStream();
			module.Write(ms, options);
			return ms.ToArray();
		}

		IEnumerable<MethodDef> AllMethods() => module.GetTypes().SelectMany(t => t.Methods).Where(m => m.HasBody && m.Body.Instructions.Count != 0);

		static void Prepare(CilBody body) => body.SimplifyBranches();

		static void Finish(CilBody body) {
			body.OptimizeBranches();
			body.UpdateInstructionOffsets();
		}

		// ---- Names

		static readonly string[] stems = {
			"‮gnp.exe",	// right-to-left override
			"class", "if", "int", "namespace", "return",
			"0a", "a b", "a.b", "<>", "<Module>", "a-b", "$",
			"Program", "System",
			"А", "ᎪᎠ",	// Cyrillic A, Cherokee lookalikes
			"\u0001\u0002", " ",
			"",
		};

		/// <summary>
		/// A unique name: a confusing stem and the counter in invisible characters (zero-width space/joiners)
		/// </summary>
		string NewName() {
			var sb = new StringBuilder(stems[nameCounter % stems.Length]);
			int n = nameCounter++;
			do {
				sb.Append("​‌‍⁠"[n & 3]);
				n >>= 2;
			} while (n != 0);
			return sb.ToString();
		}

		/// <summary>
		/// References to members of our generic types (Box&lt;int&gt;::Add) have their own names. dnlib creates a MemberRef
		/// for each generic context the method bodies use it in, so they're collected from the bodies too.
		/// </summary>
		List<(MemberRef memberRef, IMemberDef def)> CollectMemberRefsToOurDefs() {
			var list = new List<(MemberRef, IMemberDef)>();
			var seen = new HashSet<MemberRef>();
			void Add(MemberRef? mr) {
				if (mr is not null && seen.Add(mr) && mr.Resolve() is IMemberDef def && def.Module == module)
					list.Add((mr, def));
			}
			uint rows = module.TablesStream.MemberRefTable.Rows;
			for (uint rid = 1; rid <= rows; rid++)
				Add(module.ResolveMemberRef(rid));
			foreach (var method in AllMethods()) {
				foreach (var instr in method.Body.Instructions) {
					if (instr.Operand is MemberRef mr)
						Add(mr);
					else if (instr.Operand is MethodSpec ms)
						Add(ms.Method as MemberRef);
				}
			}
			return list;
		}

		static bool IsAttribute(TypeDef type) {
			for (var b = type.BaseType; b is not null; b = b.ResolveTypeDef()?.BaseType) {
				if (b.FullName == "System.Attribute")
					return true;
			}
			return false;
		}

		bool CanRename(IMemberDef member) => !keepName(member) && !literals.Contains(member.Name.String);

		void Rename(List<(MemberRef memberRef, IMemberDef def)> memberRefs) {
			foreach (var type in module.GetTypes().ToList()) {
				if (type.IsGlobalModuleType)
					continue;
				bool attribute = IsAttribute(type);
				bool delegateType = type.BaseType?.FullName == "System.MulticastDelegate";
				foreach (var method in type.Methods) {
					if (method.IsRuntimeSpecialName || method.IsVirtual || method.IsAbstract || method.IsPinvokeImpl || delegateType || !CanRename(method))
						continue;
					method.Name = NewName();
				}
				foreach (var method in type.Methods) {
					foreach (var p in method.Parameters) {
						if (p.IsNormalMethodParameter)
							p.Name = NewName();
					}
					foreach (var gp in method.GenericParameters)
						gp.Name = NewName();
				}
				if (!attribute) {
					foreach (var field in type.Fields) {
						if (!field.IsRuntimeSpecialName && CanRename(field))
							field.Name = NewName();
					}
					foreach (var property in type.Properties) {
						if (CanRename(property))
							property.Name = NewName();
					}
					foreach (var evt in type.Events) {
						if (CanRename(evt))
							evt.Name = NewName();
					}
				}
				foreach (var gp in type.GenericParameters)
					gp.Name = NewName();
				if (!attribute && CanRename(type) && !literals.Contains(type.FullName)) {
					// (Keep the generic arity: the runtime doesn't need it, but reflection names do)
					type.Name = NewName();
					if (type.DeclaringType is null)
						type.Namespace = random.Next(3) == 0 ? "" : NewName();
				}
			}
			// References to generic instances of our types and methods have their own names
			foreach (var (memberRef, def) in memberRefs)
				memberRef.Name = def.Name;
		}

		// ---- Strings

		void EncryptStrings() {
			var holder = new TypeDefUser("", "Strings", module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit,
			};
			module.Types.Add(holder);
			var decrypt = CreateDecryptMethod();
			holder.Methods.Add(decrypt);
			added.Add(decrypt);
			foreach (var method in AllMethods().ToList()) {
				if (method == decrypt)
					continue;
				var body = method.Body;
				Prepare(body);
				for (int i = 0; i < body.Instructions.Count; i++) {
					var instr = body.Instructions[i];
					if (instr.OpCode.Code != Code.Ldstr || instr.Operand is not string s)
						continue;
					int key = random.Next(1, 0x7FFF);
					var enc = new char[s.Length];
					for (int j = 0; j < s.Length; j++)
						enc[j] = (char)(s[j] ^ (key + j));
					// (The same instruction, so the branches to it are still right)
					instr.Operand = new string(enc);
					body.Instructions.Insert(i + 1, Instruction.CreateLdcI4(key));
					body.Instructions.Insert(i + 2, Instruction.Create(OpCodes.Call, decrypt));
					i += 2;
				}
				Finish(body);
			}
		}

		/// <summary>
		/// static string D(string s, int key) { var c = s.ToCharArray(); for (int i = 0; i &lt; c.Length; i++) c[i] = (char)(c[i] ^ (key + i)); return new string(c); }
		/// </summary>
		MethodDef CreateDecryptMethod() {
			var str = module.CorLibTypes.String;
			var method = new MethodDefUser("D", MethodSig.CreateStatic(str, str, module.CorLibTypes.Int32),
				MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Assembly | MethodAttributes.Static | MethodAttributes.HideBySig);
			var toCharArray = new MemberRefUser(module, "ToCharArray", MethodSig.CreateInstance(new SZArraySig(module.CorLibTypes.Char)), str.TypeDefOrRef);
			var ctor = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void, new SZArraySig(module.CorLibTypes.Char)), str.TypeDefOrRef);
			var body = method.Body = new CilBody { InitLocals = true };
			var c = new Local(new SZArraySig(module.CorLibTypes.Char));
			var i = new Local(module.CorLibTypes.Int32);
			body.Variables.Add(c);
			body.Variables.Add(i);
			var cond = Instruction.Create(OpCodes.Ldloc, i);
			var loop = Instruction.Create(OpCodes.Ldloc, c);
			var list = body.Instructions;
			list.Add(Instruction.Create(OpCodes.Ldarg_0));
			list.Add(Instruction.Create(OpCodes.Callvirt, toCharArray));
			list.Add(Instruction.Create(OpCodes.Stloc, c));
			list.Add(Instruction.Create(OpCodes.Ldc_I4_0));
			list.Add(Instruction.Create(OpCodes.Stloc, i));
			list.Add(Instruction.Create(OpCodes.Br, cond));
			list.Add(loop);
			list.Add(Instruction.Create(OpCodes.Ldloc, i));
			list.Add(Instruction.Create(OpCodes.Ldloc, c));
			list.Add(Instruction.Create(OpCodes.Ldloc, i));
			list.Add(Instruction.Create(OpCodes.Ldelem_U2));
			list.Add(Instruction.Create(OpCodes.Ldarg_1));
			list.Add(Instruction.Create(OpCodes.Ldloc, i));
			list.Add(Instruction.Create(OpCodes.Add));
			list.Add(Instruction.Create(OpCodes.Xor));
			list.Add(Instruction.Create(OpCodes.Conv_U2));
			list.Add(Instruction.Create(OpCodes.Stelem_I2));
			list.Add(Instruction.Create(OpCodes.Ldloc, i));
			list.Add(Instruction.Create(OpCodes.Ldc_I4_1));
			list.Add(Instruction.Create(OpCodes.Add));
			list.Add(Instruction.Create(OpCodes.Stloc, i));
			list.Add(cond);
			list.Add(Instruction.Create(OpCodes.Ldloc, c));
			list.Add(Instruction.Create(OpCodes.Ldlen));
			list.Add(Instruction.Create(OpCodes.Conv_I4));
			list.Add(Instruction.Create(OpCodes.Blt, loop));
			list.Add(Instruction.Create(OpCodes.Ldloc, c));
			list.Add(Instruction.Create(OpCodes.Newobj, ctor));
			list.Add(Instruction.Create(OpCodes.Ret));
			return method;
		}

		// ---- Proxy calls

		void AddProxyCalls() {
			var proxies = new Dictionary<MethodDef, MethodDef>();
			foreach (var method in AllMethods().ToList()) {
				if (added.Contains(method))
					continue;
				var body = method.Body;
				Prepare(body);
				for (int i = 0; i < body.Instructions.Count; i++) {
					var instr = body.Instructions[i];
					if (instr.OpCode.Code != Code.Call || instr.Operand is not MethodDef target || target.Module != module)
						continue;
					if (!target.IsStatic || target.HasGenericParameters || target.DeclaringType.HasGenericParameters || target.IsPinvokeImpl ||
						target.IsInternalCall || target.MethodSig.CallingConvention.HasFlag(dnlib.DotNet.CallingConvention.VarArg) || added.Contains(target))
						continue;
					if (i > 0 && body.Instructions[i - 1].OpCode.OpCodeType == OpCodeType.Prefix)
						continue;
					if (!proxies.TryGetValue(target, out var proxy)) {
						proxy = CreateProxy(target);
						proxies.Add(target, proxy);
					}
					instr.Operand = proxy;
				}
				Finish(body);
			}
		}

		MethodDef CreateProxy(MethodDef target) {
			var sig = target.MethodSig.Clone();
			var proxy = new MethodDefUser(target.Name + "_proxy", sig, MethodImplAttributes.IL | MethodImplAttributes.Managed | MethodImplAttributes.NoInlining,
				(target.Attributes & MethodAttributes.MemberAccessMask) | MethodAttributes.Static | MethodAttributes.HideBySig);
			proxy.Body = new CilBody();
			for (int i = 0; i < sig.Params.Count; i++)
				proxy.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, proxy.Parameters[i]));
			proxy.Body.Instructions.Add(Instruction.Create(OpCodes.Call, target));
			proxy.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			// (In the same type: it can call the target and use its private types)
			target.DeclaringType.Methods.Add(proxy);
			added.Add(proxy);
			return proxy;
		}

		// ---- Control flow flattening

		/// <summary>
		/// The stack depth before each instruction, -1 if it's unreachable
		/// </summary>
		static int[] StackDepths(MethodDef method) {
			var list = method.Body.Instructions;
			var index = new Dictionary<Instruction, int>();
			for (int i = 0; i < list.Count; i++)
				index[list[i]] = i;
			var depths = Enumerable.Repeat(-1, list.Count).ToArray();
			bool hasReturnValue = method.ReturnType.RemovePinnedAndModifiers().ElementType != ElementType.Void;
			var work = new Stack<int>();
			depths[0] = 0;
			work.Push(0);
			while (work.Count > 0) {
				int i = work.Pop();
				var instr = list[i];
				int d = depths[i];
				instr.UpdateStack(ref d, hasReturnValue);
				void Visit(int j, int depth) {
					if (j < list.Count && depths[j] < 0) {
						depths[j] = depth;
						work.Push(j);
					}
				}
				switch (instr.OpCode.FlowControl) {
				case FlowControl.Branch:
					Visit(index[(Instruction)instr.Operand], d);
					break;
				case FlowControl.Cond_Branch:
					if (instr.Operand is Instruction t)
						Visit(index[t], d);
					else if (instr.Operand is Instruction[] targets) {
						foreach (var target in targets)
							Visit(index[target], d);
					}
					Visit(i + 1, d);
					break;
				case FlowControl.Return:
				case FlowControl.Throw:
					break;
				default:
					Visit(i + 1, d);
					break;
				}
			}
			return depths;
		}

		void Flatten(MethodDef method) {
			var body = method.Body;
			if (added.Contains(method) || body.HasExceptionHandlers || body.Instructions.Count < 4)
				return;
			if (body.Instructions.Any(a => a.OpCode.Code == Code.Tailcall || a.OpCode.Code == Code.Jmp))
				return;
			Prepare(body);
			var list = body.Instructions;
			var depths = StackDepths(method);
			var index = new Dictionary<Instruction, int>();
			for (int i = 0; i < list.Count; i++)
				index[list[i]] = i;
			var targets = new HashSet<Instruction>();
			foreach (var instr in list) {
				if (instr.Operand is Instruction t)
					targets.Add(t);
				else if (instr.Operand is Instruction[] ts)
					targets.UnionWith(ts);
			}
			// A branch target reached with a non-empty stack (e.g. the delegate of Changed?.Invoke()'s dup;brtrue, or a
			// ternary that leaves a value): flattening dispatches blocks entered with an empty stack, so it can't move
			// such a block out. Real flatteners keep the region whole; here that method just isn't flattened.
			if (targets.Any(t => depths[index[t]] > 0))
				return;

			// Blocks start where the stack is empty: branch targets, after a branch, and every few instructions
			var starts = new List<int> { 0 };
			for (int i = 1; i < list.Count; i++) {
				if (depths[i] != 0 || list[i - 1].OpCode.OpCodeType == OpCodeType.Prefix)
					continue;
				var prevFlow = list[i - 1].OpCode.FlowControl;
				if (targets.Contains(list[i]) || prevFlow == FlowControl.Branch || prevFlow == FlowControl.Cond_Branch || prevFlow == FlowControl.Return ||
					prevFlow == FlowControl.Throw || i - starts[^1] >= 3)
					starts.Add(i);
			}
			if (starts.Count < 2)
				return;

			var blockOf = new Dictionary<Instruction, int>();
			for (int b = 0; b < starts.Count; b++)
				blockOf[list[starts[b]]] = b;
			var states = Enumerable.Range(0, starts.Count).OrderBy(_ => random.Next()).ToArray();
			int key = random.Next(1, int.MaxValue);
			var state = new Local(module.CorLibTypes.Int32);
			body.Variables.Add(state);
			body.InitLocals = true;

			var dispatch = Instruction.Create(OpCodes.Ldloc, state);
			var switchTargets = new Instruction[starts.Count];
			for (int b = 0; b < starts.Count; b++)
				switchTargets[states[b]] = list[starts[b]];
			List<Instruction> GoTo(int block) => new List<Instruction> {
				Instruction.CreateLdcI4(states[block] ^ key),
				Instruction.Create(OpCodes.Stloc, state),
				Instruction.Create(OpCodes.Br, dispatch),
			};

			var stubs = new List<Instruction>();
			Instruction Stub(Instruction target) {
				var code = GoTo(blockOf[target]);
				stubs.AddRange(code);
				return code[0];
			}

			var blocks = new List<List<Instruction>>();
			for (int b = 0; b < starts.Count; b++) {
				int end = b + 1 < starts.Count ? starts[b + 1] : list.Count;
				var code = list.Skip(starts[b]).Take(end - starts[b]).ToList();
				foreach (var instr in code) {
					// Branches to a block go through the dispatcher
					if (instr.Operand is Instruction t && blockOf.ContainsKey(t))
						instr.Operand = Stub(t);
					else if (instr.Operand is Instruction[] ts)
						instr.Operand = ts.Select(a => blockOf.ContainsKey(a) ? Stub(a) : a).ToArray();
				}
				var flow = code[^1].OpCode.FlowControl;
				if (b + 1 < starts.Count && flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw)
					code.AddRange(GoTo(b + 1));
				blocks.Add(code);
			}

			var newList = new List<Instruction>();
			newList.AddRange(GoTo(0));
			newList.Add(dispatch);
			newList.Add(Instruction.CreateLdcI4(key));
			newList.Add(Instruction.Create(OpCodes.Xor));
			newList.Add(Instruction.Create(OpCodes.Switch, switchTargets));
			newList.Add(Instruction.Create(OpCodes.Ldnull));
			newList.Add(Instruction.Create(OpCodes.Throw));
			// The blocks in any order: every way out of a block is explicit now
			foreach (var code in blocks.OrderBy(_ => random.Next()))
				newList.AddRange(code);
			newList.AddRange(stubs);
			list.Clear();
			foreach (var instr in newList)
				list.Add(instr);
			Finish(body);
		}

		// ---- Junk code

		FieldDef AddOpaqueField() {
			var holder = new TypeDefUser("", "Opaque", module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.NotPublic | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			module.Types.Add(holder);
			var field = new FieldDefUser("Zero", new FieldSig(module.CorLibTypes.Int32), FieldAttributes.Assembly | FieldAttributes.Static);
			holder.Fields.Add(field);
			return field;
		}

		/// <summary>
		/// if (Opaque.Zero != 0) { Opaque.Zero = Opaque.Zero * 7 + 1; } at the start (the field is always 0)
		/// </summary>
		void AddOpaquePredicate(MethodDef method, FieldDef opaque) {
			var body = method.Body;
			Prepare(body);
			var first = body.Instructions[0];
			var code = new[] {
				Instruction.Create(OpCodes.Ldsfld, opaque),
				Instruction.Create(OpCodes.Brfalse, first),
				Instruction.Create(OpCodes.Ldsfld, opaque),
				Instruction.CreateLdcI4(7),
				Instruction.Create(OpCodes.Mul),
				Instruction.CreateLdcI4(1),
				Instruction.Create(OpCodes.Add),
				Instruction.Create(OpCodes.Stsfld, opaque),
			};
			for (int i = 0; i < code.Length; i++)
				body.Instructions.Insert(i, code[i]);
			Finish(body);
		}

		/// <summary>
		/// After an unconditional branch, a return or a throw: dead code nothing branches to, with confusing values and
		/// a branch to itself. It's unreachable so the JIT never runs it, but it's stack-valid (real obfuscators that
		/// keep a verifiable assembly produce dead code with a computable max stack, so the module still serializes).
		/// </summary>
		void AddUnreachableJunk(MethodDef method) {
			var body = method.Body;
			// (Next to a try block or handler the JIT doesn't accept it, real obfuscators don't do it there either)
			if (body.HasExceptionHandlers)
				return;
			Prepare(body);
			var list = body.Instructions;
			int inserted = 0;
			for (int i = 0; i < list.Count && inserted < 3; i++) {
				var flow = list[i].OpCode.FlowControl;
				if (flow != FlowControl.Branch && flow != FlowControl.Return && flow != FlowControl.Throw)
					continue;
				if (list[i].OpCode.Code == Code.Leave || list[i].OpCode.Code == Code.Endfinally)
					continue;
				// Stack-neutral: push and pop, never below 0
				var junk = new List<Instruction> {
					Instruction.CreateLdcI4(random.Next()),
					Instruction.CreateLdcI4(random.Next() | 1),
					Instruction.Create(OpCodes.Div),
					Instruction.Create(OpCodes.Pop),
					Instruction.Create(OpCodes.Ldc_R8, 1.5),
					Instruction.Create(OpCodes.Conv_I8),
					Instruction.Create(OpCodes.Pop),
					Instruction.Create(OpCodes.Ldsfld, opaqueField!),
					Instruction.Create(OpCodes.Pop),
				};
				junk.Add(new Instruction(OpCodes.Br, junk[0]));
				for (int j = 0; j < junk.Count; j++)
					list.Insert(i + 1 + j, junk[j]);
				i += junk.Count;
				inserted++;
			}
			Finish(body);
		}
		FieldDef? opaqueField;

		// ---- Junk types

		void AddJunkTypes() {
			var obj = module.CorLibTypes.Object.TypeDefOrRef;
			var i32 = module.CorLibTypes.Int32;

			// Obfuscator marks
			var confusedBy = new TypeDefUser("", "ConfusedByAttribute", module.CorLibTypes.GetTypeRef("System", "Attribute"));
			module.Types.Add(confusedBy);
			var suppressIldasm = new MemberRefUser(module, ".ctor", MethodSig.CreateInstance(module.CorLibTypes.Void),
				module.CorLibTypes.GetTypeRef("System.Runtime.CompilerServices", "SuppressIldasmAttribute"));
			module.CustomAttributes.Add(new CustomAttribute(suppressIldasm));

			// 30 levels of nested types
			TypeDef parent = new TypeDefUser("", "Nest", obj) { Attributes = TypeAttributes.Public };
			module.Types.Add(parent);
			for (int i = 0; i < 30; i++) {
				var nested = new TypeDefUser("", "Nest" + i, obj) { Attributes = TypeAttributes.NestedPublic };
				parent.NestedTypes.Add(nested);
				parent = nested;
			}

			var big = new TypeDefUser("", "Big", obj) { Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed };
			module.Types.Add(big);

			// 300 parameters
			var many = new MethodDefUser("ManyParameters", MethodSig.CreateStatic(i32, Enumerable.Repeat((TypeSig)i32, 300).ToArray()),
				MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static);
			many.Body = new CilBody();
			many.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, many.Parameters[0]));
			for (int i = 1; i < 300; i++) {
				many.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg, many.Parameters[i]));
				many.Body.Instructions.Add(Instruction.Create(OpCodes.Add));
			}
			many.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
			big.Methods.Add(many);
			added.Add(many);

			// A huge method: a 4000 case switch over expressions
			var huge = new MethodDefUser("Huge", MethodSig.CreateStatic(i32, i32), MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static);
			huge.Body = new CilBody();
			var ret = Instruction.Create(OpCodes.Ret);
			var cases = new Instruction[4000];
			var caseCode = new List<Instruction>();
			for (int i = 0; i < cases.Length; i++) {
				cases[i] = Instruction.CreateLdcI4(i * 31);
				caseCode.Add(cases[i]);
				caseCode.Add(Instruction.Create(OpCodes.Ldarg_0));
				caseCode.Add(Instruction.Create(OpCodes.Xor));
				caseCode.Add(Instruction.Create(OpCodes.Br, ret));
			}
			huge.Body.Instructions.Add(Instruction.Create(OpCodes.Ldarg_0));
			huge.Body.Instructions.Add(Instruction.Create(OpCodes.Switch, cases));
			huge.Body.Instructions.Add(Instruction.CreateLdcI4(-1));
			huge.Body.Instructions.Add(Instruction.Create(OpCodes.Br, ret));
			foreach (var instr in caseCode)
				huge.Body.Instructions.Add(instr);
			huge.Body.Instructions.Add(ret);
			big.Methods.Add(huge);
			added.Add(huge);

			// 40 generic parameters, each constrained to the previous one
			var generic = new TypeDefUser("", "Generic`40", obj) { Attributes = TypeAttributes.Public };
			for (int i = 0; i < 40; i++) {
				var gp = new GenericParamUser((ushort)i, GenericParamAttributes.NonVariant, "T" + i);
				if (i > 0)
					gp.GenericParamConstraints.Add(new GenericParamConstraintUser(new TypeSpecUser(new GenericVar(i - 1, generic))));
				generic.GenericParameters.Add(gp);
			}
			module.Types.Add(generic);

			// Members with the same name: methods that differ only by the return type, a field and a property named like them
			var same = new TypeDefUser("", "Same", obj) { Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed };
			module.Types.Add(same);
			foreach (var type in new TypeSig[] { i32, module.CorLibTypes.String, module.CorLibTypes.Boolean }) {
				var m = new MethodDefUser("a", MethodSig.CreateStatic(type), MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static);
				m.Body = new CilBody();
				m.Body.Instructions.Add(type.ElementType == ElementType.String ? Instruction.Create(OpCodes.Ldnull) : Instruction.CreateLdcI4(0));
				m.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
				same.Methods.Add(m);
				added.Add(m);
			}
			same.Fields.Add(new FieldDefUser("a", new FieldSig(i32), FieldAttributes.Public | FieldAttributes.Static));
		}
	}
}
