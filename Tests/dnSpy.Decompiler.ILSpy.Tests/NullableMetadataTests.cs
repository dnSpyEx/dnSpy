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
using dnSpy.AsmEditor.Compiler;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// <see cref="NullableMetadata"/> reads the compiler's [Nullable] bytes the way the compiler writes them
	/// </summary>
	public class NullableMetadataTests {
		const string Source = """
			using System.Collections.Generic;
			namespace NullableEncoding {
				public class Outer<T> { public class Inner<U> { } public struct InnerValue<U> { } }
				public unsafe class Fields<T, TClass> where TClass : class {
					// Each type mixes '?' and not annotated so the compiler writes a byte array
					public Dictionary<string, string?> F1 = null!;
					public Dictionary<int, string?>? F2;
					public KeyValuePair<string?, int> F3;
					public KeyValuePair<string, string?>? F4;
					public (string?, int, string) F5;
					public (int, int, string?, string)[]? F6;
					public string?[] F7 = null!;
					public int?[]? F8;
					public List<int?>? F9;
					public Outer<string?>.Inner<string> F10 = null!;
					public Outer<string>.InnerValue<string?> F11;
					public List<T>? F12;
					public List<TClass?> F13 = null!;
					public Dictionary<KeyValuePair<int, string?>, List<string>> F14 = null!;
					public int* F15;
					public List<string?>[][]? F16;
					public System.Collections.Generic.List<(string?, (int, string))> F17 = null!;
					public KeyValuePair<int, int>? F18;
					public List<KeyValuePair<int, int>>? F19;
				}
				public class Derived1 : List<string?> { }
				public class Derived2 : Dictionary<string, string?>, IEnumerable<KeyValuePair<string, string?>>, System.IComparable<string> { public int CompareTo(string? s) => 0; }
				public class Derived3 : List<int> { }
			#nullable disable
				public class Derived4 : List<string>, System.IComparable<string> { public int CompareTo(string s) => 0; }
			#nullable restore
			#nullable disable
				public class Oblivious<T> {
					public string F1;
					public List<string> F2;
					public int F3;
					public KeyValuePair<int, int> F4;
					public string M(string s, int i) => s;
				}
			#nullable restore
				public class Mixed {
					public string? F1;
					public int F2;
					public List<int>? F3;
			#nullable disable
					public string F4;
					public List<string> F5;
			#nullable restore
					public Dictionary<string, string> F6 = null!;
			#nullable disable annotations
					public Dictionary<string, string> F7;
			#nullable restore
				}
			}
			""";

		static readonly ModuleDef module = ModuleDefMD.Load(TestCompiler.Compile("NullableEncoding", Source).Image);

		static TypeDef GetType(string name) => module.GetTypes().First(a => a.Name == name);

		[Fact]
		public void Positions_MatchTheCompilersByteArrays() {
			var checkedFields = new List<string>();
			foreach (var field in GetType("Fields`2").Fields) {
				var ca = field.CustomAttributes.Find(NullableMetadata.NullableAttribute);
				var positions = NullableMetadata.GetPositions(field.FieldSig.Type);
				Assert.NotNull(positions);
				if (ca?.ConstructorArguments[0].Value is IList<CAArgument> bytes) {
					Assert.True(bytes.Count == positions!.Count, $"{field.Name}: {field.FieldSig.Type}: compiler wrote {bytes.Count} bytes, expected {positions!.Count}");
					// The placeholders of generic value types are 0, the other positions are annotated or not
					for (int i = 0; i < bytes.Count; i++)
						Assert.True(positions[i] == ((byte)bytes[i].Value! != 0), $"{field.Name}: position {i}");
					checkedFields.Add(field.Name);
				}
				else
					Assert.False(positions!.Contains(true) && positions.Contains(false) && positions.Count > 1, $"{field.Name}: expected a byte array");
			}
			Assert.True(checkedFields.Count >= 14, string.Join(", ", checkedFields));
		}

		[Fact]
		public void BaseTypesAndInterfaces() {
			bool IsOblivious(string name) {
				var type = GetType(name);
				byte context = NullableMetadata.GetContext(type);
				if (NullableMetadata.HasObliviousPositions(type.BaseType.ToTypeSig(), type, context, isBaseType: true))
					return true;
				return type.Interfaces.Any(a => NullableMetadata.HasObliviousPositions(a.Interface.ToTypeSig(), a, context, isBaseType: true));
			}
			foreach (var name in new[] { "Derived1", "Derived2", "Derived4" }) {
				var type = GetType(name);
				foreach (var (owner, sig) in new[] { ((IHasCustomAttribute)type, type.BaseType.ToTypeSig()) }.Concat(type.Interfaces.Select(a => ((IHasCustomAttribute)a, a.Interface.ToTypeSig())))) {
					if (owner.CustomAttributes.Find(NullableMetadata.NullableAttribute)?.ConstructorArguments[0].Value is IList<CAArgument> bytes)
						Assert.True(bytes.Count == NullableMetadata.GetPositions(sig)!.Count, $"{name}: {sig}");
				}
			}
			Assert.False(IsOblivious("Derived1"));
			Assert.False(IsOblivious("Derived2"));
			Assert.False(IsOblivious("Derived3"));
			Assert.True(IsOblivious("Derived4"));
			Assert.False(NullableMetadata.HasAnnotatablePositions(GetType("Derived3").BaseType.ToTypeSig(), isBaseType: true));
			Assert.True(NullableMetadata.HasAnnotatablePositions(GetType("Derived1").BaseType.ToTypeSig(), isBaseType: true));
		}

		[Fact]
		public void ObliviousPositions() {
			var oblivious = GetType("Oblivious`1");
			byte context = NullableMetadata.GetContext(oblivious);
			Assert.Equal(0, context);
			bool IsOblivious(TypeDef type, string name) {
				var field = type.FindField(name);
				return NullableMetadata.HasObliviousPositions(field.FieldSig.Type, field, NullableMetadata.GetContext(type));
			}
			Assert.True(IsOblivious(oblivious, "F1"));
			Assert.True(IsOblivious(oblivious, "F2"));
			Assert.False(IsOblivious(oblivious, "F3"));
			Assert.False(IsOblivious(oblivious, "F4"));
			var method = oblivious.FindMethod("M");
			Assert.True(NullableMetadata.HasObliviousPositions(method.MethodSig.RetType, method.Parameters.ReturnParameter.ParamDef, NullableMetadata.GetOwnContext(method) ?? context));
			Assert.True(NullableMetadata.HasObliviousPositions(oblivious.GenericParameters[0], context));

			var mixed = GetType("Mixed");
			Assert.False(IsOblivious(mixed, "F1"));
			Assert.False(IsOblivious(mixed, "F2"));
			Assert.False(IsOblivious(mixed, "F3"));
			Assert.True(IsOblivious(mixed, "F4"));
			Assert.True(IsOblivious(mixed, "F5"));
			Assert.False(IsOblivious(mixed, "F6"));
			Assert.True(IsOblivious(mixed, "F7"));

			var fields = GetType("Fields`2");
			Assert.False(NullableMetadata.HasObliviousPositions(fields.GenericParameters[0], NullableMetadata.GetContext(fields)));
			Assert.False(NullableMetadata.HasObliviousPositions(fields.GenericParameters[1], NullableMetadata.GetContext(fields)));
			foreach (var field in fields.Fields)
				Assert.False(NullableMetadata.HasObliviousPositions(field.FieldSig.Type, field, NullableMetadata.GetContext(fields)), field.Name);
		}
	}
}
