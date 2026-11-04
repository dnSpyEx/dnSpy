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
using System.IO;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// Writes synthetic samples that use tricks obfuscators and anti-decompiler tools use. They're a stand-in for
	/// real protected/malware samples (which should be added to the corpus too).
	/// </summary>
	static class SampleGenerator {
		public static int Generate(string outDir, string baseAssembly) {
			Directory.CreateDirectory(outDir);
			var baseName = Path.GetFileNameWithoutExtension(baseAssembly);
			Write(outDir, baseName, "control", baseAssembly, m => { });
			Write(outDir, baseName, "unicode-names", baseAssembly, m => Rename(m, i => "‮​" + new string((char)(0x4E00 + i % 0x5000), 1) + "\u0001" + i));
			Write(outDir, baseName, "long-names", baseAssembly, m => Rename(m, i => new string('A', 4000) + i));
			Write(outDir, baseName, "keyword-names", baseAssembly, m => Rename(m, i => new[] { "class", "int", "void", "this", "<Module>", "a.b", "a b", "" }[i % 8] + (i % 8 == 7 ? "" : i.ToString())));
			Write(outDir, baseName, "dead-code-garbage", baseAssembly, AddDeadCodeGarbage);
			Write(outDir, baseName, "deep-nesting", baseAssembly, AddDeepNesting);
			Write(outDir, baseName, "huge-method", baseAssembly, AddHugeMethod);
			Write(outDir, baseName, "missing-references", baseAssembly, AddMissingReferences);
			Write(outDir, baseName, "metadata-tricks", baseAssembly, m => { }, MetadataTricks);
			return 0;
		}

		static void Write(string outDir, string baseName, string variant, string baseAssembly, Action<ModuleDefMD> mutate, Action<ModuleWriterOptions>? configure = null) {
			var module = ModuleDefMD.Load(baseAssembly);
			mutate(module);
			var options = new ModuleWriterOptions(module) { Logger = DummyLogger.NoThrowInstance };
			options.MetadataOptions.Flags |= MetadataFlags.KeepOldMaxStack;
			configure?.Invoke(options);
			var file = Path.Combine(outDir, $"{baseName}.{variant}.dll");
			module.Write(file, options);
			Console.WriteLine($"Wrote {file}");
		}

		static void Rename(ModuleDef module, Func<int, string> newName) {
			int i = 0;
			foreach (var type in module.GetTypes().ToList()) {
				if (type.IsGlobalModuleType)
					continue;
				type.Name = newName(i++);
				if (type.DeclaringType is null)
					type.Namespace = UTF8String.Empty;
				foreach (var method in type.Methods) {
					if (!method.IsSpecialName && !method.IsRuntimeSpecialName)
						method.Name = newName(i++);
					foreach (var p in method.ParamDefs)
						p.Name = newName(i++);
				}
				foreach (var field in type.Fields) {
					if (!field.IsRuntimeSpecialName)
						field.Name = newName(i++);
				}
				foreach (var property in type.Properties)
					property.Name = newName(i++);
				foreach (var ev in type.Events)
					ev.Name = newName(i++);
			}
		}

		/// <summary>
		/// Unreachable invalid code at the start of every method: a branch over IL that underflows the stack
		/// </summary>
		static void AddDeadCodeGarbage(ModuleDef module) {
			foreach (var method in module.GetTypes().SelectMany(t => t.Methods)) {
				var body = method.Body;
				if (body is null || body.Instructions.Count == 0)
					continue;
				var first = body.Instructions[0];
				body.Instructions.Insert(0, OpCodes.Br.ToInstruction(first));
				body.Instructions.Insert(1, OpCodes.Pop.ToInstruction());
				body.Instructions.Insert(2, OpCodes.Pop.ToInstruction());
				body.Instructions.Insert(3, OpCodes.Add.ToInstruction());
				body.Instructions.Insert(4, OpCodes.Ret.ToInstruction());
			}
		}

		/// <summary>
		/// Expressions and blocks nested thousands of levels deep: recursive visitors overflow small stacks
		/// </summary>
		static void AddDeepNesting(ModuleDef module) {
			var type = new TypeDefUser("Stress", "Deep", module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			module.Types.Add(type);

			var sum = NewStaticMethod(module, "Sum", module.CorLibTypes.Int32, module.CorLibTypes.Int32);
			var il = sum.Body.Instructions;
			il.Add(OpCodes.Ldarg_0.ToInstruction());
			for (int i = 0; i < 20000; i++) {
				il.Add(OpCodes.Ldc_I4.ToInstruction(i));
				il.Add(OpCodes.Add.ToInstruction());
			}
			il.Add(OpCodes.Ret.ToInstruction());
			type.Methods.Add(sum);

			var ifs = NewStaticMethod(module, "NestedIfs", module.CorLibTypes.Int32, module.CorLibTypes.Int32);
			il = ifs.Body.Instructions;
			var end = OpCodes.Ldarg_0.ToInstruction();
			for (int i = 0; i < 3000; i++) {
				il.Add(OpCodes.Ldarg_0.ToInstruction());
				il.Add(OpCodes.Ldc_I4.ToInstruction(i));
				il.Add(OpCodes.Ble.ToInstruction(end));
			}
			il.Add(OpCodes.Ldc_I4_1.ToInstruction());
			il.Add(OpCodes.Starg.ToInstruction(ifs.Parameters[0]));
			il.Add(end);
			il.Add(OpCodes.Ret.ToInstruction());
			type.Methods.Add(ifs);
		}

		static void AddHugeMethod(ModuleDef module) {
			var type = new TypeDefUser("Stress", "Huge", module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			module.Types.Add(type);
			var method = NewStaticMethod(module, "Compute", module.CorLibTypes.Int32, module.CorLibTypes.Int32);
			var il = method.Body.Instructions;
			for (int i = 0; i < 20000; i++) {
				il.Add(OpCodes.Ldarg_0.ToInstruction());
				il.Add(OpCodes.Ldc_I4_3.ToInstruction());
				il.Add(OpCodes.Mul.ToInstruction());
				il.Add(OpCodes.Ldc_I4.ToInstruction(i));
				il.Add(OpCodes.Xor.ToInstruction());
				il.Add(OpCodes.Starg.ToInstruction(method.Parameters[0]));
			}
			il.Add(OpCodes.Ldarg_0.ToInstruction());
			il.Add(OpCodes.Ret.ToInstruction());
			type.Methods.Add(method);
		}

		/// <summary>
		/// References to an assembly that doesn't exist (like samples whose dependencies weren't collected)
		/// </summary>
		static void AddMissingReferences(ModuleDef module) {
			var ghostAsm = new AssemblyRefUser("Ghost.Runtime", new Version(1, 2, 3, 4));
			var ghostType = new TypeRefUser(module, "Ghost", "Api", ghostAsm);
			var doIt = new MemberRefUser(module, "Do", MethodSig.CreateStatic(module.CorLibTypes.Void, new ClassSig(ghostType)), ghostType);
			var type = new TypeDefUser("Stress", "UsesGhost", new TypeRefUser(module, "Ghost", "BaseClass", ghostAsm)) {
				Attributes = TypeAttributes.Public,
			};
			module.Types.Add(type);
			type.Fields.Add(new FieldDefUser("api", new FieldSig(new ClassSig(ghostType)), FieldAttributes.Public));
			var method = new MethodDefUser("Run", MethodSig.CreateInstance(module.CorLibTypes.Void), MethodImplAttributes.IL, MethodAttributes.Public | MethodAttributes.HideBySig) {
				Body = new CilBody(),
			};
			method.Body.Instructions.Add(OpCodes.Ldarg_0.ToInstruction());
			method.Body.Instructions.Add(OpCodes.Ldfld.ToInstruction(type.Fields[0]));
			method.Body.Instructions.Add(OpCodes.Call.ToInstruction(doIt));
			method.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
			type.Methods.Add(method);
		}

		/// <summary>
		/// Tables stream extra data and HasDelete flag, a junk stream and a fake #Strings stream before the real one
		/// </summary>
		static void MetadataTricks(ModuleWriterOptions options) {
			options.MetadataOptions.TablesHeapOptions.ExtraData = 0xDEADBEEF;
			options.MetadataOptions.TablesHeapOptions.HasDeletedRows = true;
			var junk = new RawHeap("#Junk", 0x100);
			var fakeStrings = new RawHeap("#Strings", 0x2000);
			options.MetadataOptions.CustomHeaps.Add(junk);
			options.MetadataOptions.CustomHeaps.Add(fakeStrings);
			options.MetadataOptions.MetadataHeapsAdded += (s, e) => {
				e.Heaps.Remove(fakeStrings);
				e.Heaps.Insert(0, fakeStrings);
			};
		}

		static MethodDefUser NewStaticMethod(ModuleDef module, string name, TypeSig returnType, TypeSig parameterType) {
			var method = new MethodDefUser(name, MethodSig.CreateStatic(returnType, parameterType), MethodImplAttributes.IL,
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) {
				Body = new CilBody { MaxStack = 8, KeepOldMaxStack = true },
			};
			return method;
		}

		sealed class RawHeap : HeapBase {
			readonly string name;
			readonly byte[] data;

			public RawHeap(string name, int size) {
				this.name = name;
				data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Range(0, size / 8 + 1).Select(i => "Junk\0Bad"))).Take(size).ToArray();
			}

			public override string Name => name;
			public override uint GetRawLength() => (uint)data.Length;
			protected override void WriteToImpl(DataWriter writer) => writer.WriteBytes(data);
		}
	}
}
