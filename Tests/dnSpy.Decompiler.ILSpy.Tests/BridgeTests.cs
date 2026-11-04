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
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Writer;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	public class BridgeTests {
		readonly ITestOutputHelper log;

		public BridgeTests(ITestOutputHelper log) => this.log = log;

		[Fact]
		public void UnmodifiedModule_UsesOriginalImage_WithIdentityTokens() {
			var module = TestCompiler.Compile(nameof(UnmodifiedModule_UsesOriginalImage_WithIdentityTokens), TestPrograms.ControlFlow).Load();
			var bridged = ModuleBridge.Create(module, isModified: false, new ModuleBridgeOptions());
			foreach (var reason in bridged.NormalizeReasons)
				log.WriteLine("Normalized: " + reason);

			Assert.Equal(BridgeImageKind.Original, bridged.ImageKind);
			Assert.Empty(bridged.NormalizeReasons);
			foreach (var type in module.GetTypes()) {
				AssertRoundTrips(bridged, type);
				foreach (var method in type.Methods)
					AssertRoundTrips(bridged, method);
				foreach (var field in type.Fields)
					AssertRoundTrips(bridged, field);
			}
		}

		[Fact]
		public void ModifiedModule_IsNormalized_AndNewMembersAreMapped() {
			var module = TestCompiler.Compile(nameof(ModifiedModule_IsNormalized_AndNewMembersAreMapped), TestPrograms.ControlFlow).Load();
			var existing = module.Find("ControlFlow", false);
			uint existingToken = existing.MDToken.Raw;

			// Simulate an edit in dnSpy: add a type with a method
			var added = new TypeDefUser("Added", "NewType", module.CorLibTypes.Object.TypeDefOrRef) {
				Attributes = TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.Abstract | TypeAttributes.Sealed,
			};
			var answer = new MethodDefUser("Answer", MethodSig.CreateStatic(module.CorLibTypes.Int32),
				MethodImplAttributes.IL | MethodImplAttributes.Managed, MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig);
			answer.Body = new CilBody();
			answer.Body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(42));
			answer.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
			added.Methods.Add(answer);
			module.Types.Add(added);

			var decompiler = TestDecompiler.Create(isModified: _ => true);
			var result = decompiler.DecompileType(added);
			log.WriteLine(result.Text);

			Assert.Equal(BridgeImageKind.Normalized, result.Module.ImageKind);
			Assert.Contains("Module was modified in memory", result.Module.NormalizeReasons);
			Assert.Contains("public static int Answer()", result.Text);
			Assert.Contains("return 42;", result.Text);
			AssertRoundTrips(result.Module, added);
			AssertRoundTrips(result.Module, answer);
			// Rids were preserved: existing members keep the tokens dnSpy shows
			Assert.Equal((int)existingToken, result.Module.Tokens.GetToken(existing));
		}

		[Fact]
		public void DuplicateStringsStream_IsNormalized_AndNamesMatchDnlib() {
			var image = WriteWithFakeStringsHeapFirst(TestCompiler.Compile(nameof(DuplicateStringsStream_IsNormalized_AndNamesMatchDnlib), TestPrograms.ControlFlow).Load());
			var module = ModuleDefMD.Load(image, TestCompiler.CreateModuleContext());
			// dnlib (like the CLR) uses the last #Strings stream, so it still sees the real names
			var type = module.Find("ControlFlow", false);
			Assert.NotNull(type);

			var decompiler = TestDecompiler.Create();
			var result = decompiler.DecompileType(type);
			foreach (var reason in result.Module.NormalizeReasons)
				log.WriteLine("Normalized: " + reason);

			Assert.Equal(BridgeImageKind.Normalized, result.Module.ImageKind);
			Assert.Contains(result.Module.NormalizeReasons, r => r.Contains("Duplicate metadata stream '#Strings'"));
			Assert.Contains("public class ControlFlow", result.Text);
			Assert.Contains("public int Loops(int[] values)", result.Text);
		}

		/// <summary>
		/// Documents (and guards) that SRM picks the same #Strings stream as dnlib and the CLR (the last one) when
		/// there are duplicates. If this ever changes, the anomaly detector must keep normalizing these files.
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void DuplicateStringsStreams_SrmAndDnlibUseTheSameStream(bool fakeFirst) {
			var image = WriteWithFakeStringsHeap(TestCompiler.Compile(nameof(DuplicateStringsStreams_SrmAndDnlibUseTheSameStream) + fakeFirst, TestPrograms.ControlFlow).Load(), fakeFirst);
			var module = ModuleDefMD.Load(image, TestCompiler.CreateModuleContext());
			using var pe = new PEReader(new MemoryStream(image));
			var srm = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);
			var dnlibNames = module.GetTypes().Select(t => t.Name.String).ToArray();
			var srmNames = srm.TypeDefinitions.Select(h => srm.GetString(srm.GetTypeDefinition(h).Name)).ToArray();
			log.WriteLine($"dnlib: {string.Join(", ", dnlibNames)}");
			log.WriteLine($"SRM:   {string.Join(", ", srmNames)}");

			Assert.Equal(dnlibNames.OrderBy(n => n), srmNames.OrderBy(n => n));
			Assert.Empty(MetadataAnomalyDetector.CompareWithDnlib(module, srm));
			Assert.Contains(MetadataAnomalyDetector.GetStructuralAnomalies(module), a => a.Contains("Duplicate metadata stream '#Strings'"));
		}

		[Fact]
		public void DifferentialCheck_ReportsNamesSrmReadsDifferently() {
			var compiled = TestCompiler.Compile(nameof(DifferentialCheck_ReportsNamesSrmReadsDifferently), TestPrograms.ControlFlow);
			var module = compiled.Load();
			// What SRM reads: the same image with one type name patched in place (same length, so all heap sizes are identical)
			var patched = (byte[])compiled.Image.Clone();
			var find = System.Text.Encoding.ASCII.GetBytes("ControlFlow\0");
			int patches = 0;
			for (int i = 0; i + find.Length <= patched.Length; i++) {
				if (patched.AsSpan(i, find.Length).SequenceEqual(find)) {
					patched[i + find.Length - 2] = (byte)'x';
					patches++;
				}
			}
			Assert.NotEqual(0, patches);
			using var pe = new PEReader(new MemoryStream(patched));
			var srm = System.Reflection.Metadata.PEReaderExtensions.GetMetadataReader(pe);

			var differences = MetadataAnomalyDetector.CompareWithDnlib(module, srm);
			foreach (var d in differences)
				log.WriteLine(d);
			Assert.Contains(differences, d => d.StartsWith("Type name mismatch"));
		}

		[Theory]
		[InlineData(false, true)]
		[InlineData(true, true)]
		[InlineData(true, false)]
		public void EveryKindOfDefinition_RoundTrips(bool normalize, bool preserveRids) {
			var module = TestCompiler.Compile(nameof(EveryKindOfDefinition_RoundTrips) + normalize + preserveRids, TestPrograms.Members).Load();
			var bridged = ModuleBridge.Create(module, isModified: false, new ModuleBridgeOptions { AlwaysNormalize = normalize, PreserveRids = preserveRids });
			Assert.Equal(normalize ? BridgeImageKind.Normalized : BridgeImageKind.Original, bridged.ImageKind);

			int count = 0;
			var failures = new List<string>();
			void Check(IMDTokenProvider def) {
				int token = bridged.Tokens.GetToken(def);
				if (token == 0 || !ReferenceEquals(bridged.Tokens.Resolve(token), def))
					failures.Add($"{def.GetType().Name} {def} 0x{def.MDToken.Raw:X8} -> 0x{token:X8} -> {(token == 0 ? null : bridged.Tokens.Resolve(token))}");
				count++;
			}
			foreach (var type in module.GetTypes()) {
				Check(type);
				foreach (var gp in type.GenericParameters)
					Check(gp);
				foreach (var field in type.Fields)
					Check(field);
				foreach (var method in type.Methods) {
					Check(method);
					foreach (var pd in method.ParamDefs)
						Check(pd);
					foreach (var gp in method.GenericParameters)
						Check(gp);
				}
				foreach (var property in type.Properties)
					Check(property);
				foreach (var ev in type.Events)
					Check(ev);
			}
			foreach (var f in failures)
				log.WriteLine("Doesn't round-trip: " + f);
			log.WriteLine($"{count} definitions checked");
			Assert.Empty(failures);
			Assert.True(count >= 30);

			var decompiler = new UpstreamCSharpDecompiler(new ModuleBridgeCache(new ModuleBridgeOptions { AlwaysNormalize = normalize, PreserveRids = preserveRids }), TestDecompiler.CreateSettings());
			var result = decompiler.DecompileType(module.Find("Container`1", false));
			Assert.Contains("public event EventHandler? Changed;", result.Text);
			Assert.Contains("public U Convert<U>(T value, Func<T, U> converter) where U : struct", result.Text);
			// References to members of the type map back to the right definitions
			var converter = module.Find("Container`1", false).FindMethod("Convert");
			Assert.Contains(result.References, r => r.Target is ICSharpCode.Decompiler.TypeSystem.IEntity e && decompiler.Cache.ToDnlib(e) == converter);
		}

		[Fact]
		public void EditedMethodBody_IsDecompiledFromTheEdit() {
			var module = TestCompiler.Compile(nameof(EditedMethodBody_IsDecompiledFromTheEdit), TestPrograms.Members).Load();
			var answer = module.Find("Constants", false).FindMethod("Answer");
			var ldc = answer.Body.Instructions.Single(i => i.IsLdcI4());
			ldc.OpCode = OpCodes.Ldc_I4;
			ldc.Operand = 1234;

			var result = TestDecompiler.Create(isModified: _ => true).DecompileType(answer.DeclaringType);
			log.WriteLine(result.Text);
			Assert.Equal(BridgeImageKind.Normalized, result.Module.ImageKind);
			Assert.Contains("return 1234;", result.Text);
		}

		/// <summary>
		/// dnSpy doesn't reload a module after saving it, so its original image is stale. Added members are detected
		/// even if the caller says the module isn't modified (body-only edits must be reported by the caller).
		/// </summary>
		[Fact]
		public void MembersNotInTheOriginalImage_ForceNormalization() {
			var module = TestCompiler.Compile(nameof(MembersNotInTheOriginalImage_ForceNormalization), TestPrograms.Members).Load();
			var type = module.Find("Constants", false);
			var added = new MethodDefUser("Added", MethodSig.CreateStatic(module.CorLibTypes.Int32), MethodImplAttributes.IL | MethodImplAttributes.Managed,
				MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig) { Body = new CilBody() };
			added.Body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(7));
			added.Body.Instructions.Add(OpCodes.Ret.ToInstruction());
			type.Methods.Add(added);

			var result = TestDecompiler.Create(isModified: _ => false).DecompileType(type);
			foreach (var reason in result.Module.NormalizeReasons)
				log.WriteLine("Normalized: " + reason);
			Assert.Equal(BridgeImageKind.Normalized, result.Module.ImageKind);
			Assert.Contains("Module has members that aren't in its original image", result.Module.NormalizeReasons);
			Assert.Contains("public static int Added()", result.Text);
			// The identity map never hands out a token for an object that isn't in the image
			Assert.Equal(0, new IdentityTokenMap(module).GetToken(added));
		}

		[Fact]
		public void Invalidate_RebuildsTheImageAndTheTypeSystem() {
			var module = TestCompiler.Compile(nameof(Invalidate_RebuildsTheImageAndTheTypeSystem), TestPrograms.Members).Load();
			bool modified = false;
			var decompiler = TestDecompiler.Create(isModified: _ => modified);
			var type = module.Find("Constants", false);
			var before = decompiler.DecompileType(type);
			Assert.Contains("return 42;", before.Text);
			Assert.Equal(BridgeImageKind.Original, before.Module.ImageKind);

			var ldc = type.FindMethod("Answer").Body.Instructions.Single(i => i.IsLdcI4());
			ldc.OpCode = OpCodes.Ldc_I4;
			ldc.Operand = 4321;
			modified = true;
			int generation = decompiler.Cache.Generation;
			decompiler.Cache.Invalidate(module);
			Assert.True(decompiler.Cache.Generation > generation);

			var after = decompiler.DecompileType(type);
			Assert.NotSame(before.Module, after.Module);
			Assert.Equal(BridgeImageKind.Normalized, after.Module.ImageKind);
			Assert.Contains("return 4321;", after.Text);
		}

		[Fact]
		public void WriterErrors_AreReported() {
			var module = TestCompiler.Compile(nameof(WriterErrors_AreReported), TestPrograms.Members).Load();
			var answer = module.Find("Constants", false).FindMethod("Answer");
			// A branch to an instruction that isn't in the method: dnlib logs an error and writes something anyway
			answer.Body.Instructions.Insert(0, OpCodes.Br.ToInstruction(OpCodes.Nop.ToInstruction()));

			var bridged = ModuleBridge.Create(module, isModified: true, new ModuleBridgeOptions());
			foreach (var e in bridged.WriterErrors)
				log.WriteLine("Writer error: " + e);
			Assert.NotEmpty(bridged.WriterErrors);
			Assert.Contains(bridged.NormalizeReasons, r => r.StartsWith("dnlib reported"));
		}

		static void AssertRoundTrips(BridgedModule bridged, IMDTokenProvider definition) {
			int token = bridged.Tokens.GetToken(definition);
			Assert.NotEqual(0, token);
			Assert.Same(definition, bridged.Tokens.Resolve(token));
		}

		/// <summary>
		/// Classic anti-decompiler trick: an extra #Strings stream before the real one. The CLR and dnlib use the
		/// last one, readers that use the first one see garbage names.
		/// </summary>
		static byte[] WriteWithFakeStringsHeapFirst(ModuleDef module) => WriteWithFakeStringsHeap(module, fakeFirst: true);

		static byte[] WriteWithFakeStringsHeap(ModuleDef module, bool fakeFirst) {
			var options = new ModuleWriterOptions(module);
			var fake = new FakeHeap("#Strings", 0x4000);
			options.MetadataOptions.CustomHeaps.Add(fake);
			options.MetadataOptions.MetadataHeapsAdded += (s, e) => {
				e.Heaps.Remove(fake);
				if (fakeFirst)
					e.Heaps.Insert(0, fake);
				else
					e.Heaps.Add(fake);
			};
			var stream = new MemoryStream();
			module.Write(stream, options);
			return stream.ToArray();
		}

		sealed class FakeHeap : HeapBase {
			readonly string name;
			readonly byte[] data;

			public FakeHeap(string name, int size) {
				this.name = name;
				data = Enumerable.Range(0, size).Select(i => i % 7 == 0 ? (byte)0 : (byte)('a' + i % 26)).ToArray();
			}

			public override string Name => name;
			public override uint GetRawLength() => (uint)data.Length;
			protected override void WriteToImpl(DataWriter writer) => writer.WriteBytes(data);
		}
	}
}
