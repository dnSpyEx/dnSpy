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
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Edit code on a real netstandard 2.0 library (Newtonsoft.Json 13.0.4) with the references that dnSpy gives the
	/// compiler: netstandard 2.0's reference assembly (dnSpy with .NET installed, or a .NET Framework 4.x mscorlib)
	/// doesn't have the attributes that the compiler embedded in the library (NullableAttribute,
	/// NullableContextAttribute), and the edited code is compiled to a netmodule, which can't embed them (CS0518 in
	/// every method with nullable annotations). A newer framework's references have public ones.
	/// </summary>
	public class EditReferenceSetTests {
		static string SamplesDir => Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0");

		static ModuleDefMD LoadNewtonsoft(bool netStandardReferenceAssembly) {
			var context = new ModuleContext();
			var resolver = new AssemblyResolver(context) { FindExactMatch = false, EnableFrameworkRedirect = false };
			if (netStandardReferenceAssembly)
				resolver.PreSearchPaths.Add(Path.Combine(SamplesDir, "ref"));
			resolver.PreSearchPaths.Add(SamplesDir);
			resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			return ModuleDefMD.Load(Path.Combine(SamplesDir, "Newtonsoft.Json.dll"), context);
		}

		static IMemberDef Find(ModuleDef module, string member) => member switch {
			"JPropertyList.Count" => module.Find("Newtonsoft.Json.Linq.JProperty/JPropertyList", false).FindProperty("Count").GetMethod,
			"JToken.SelectTokens" => module.Find("Newtonsoft.Json.Linq.JToken", false).Methods.Single(a =>
				a.Name == "SelectTokens" && a.MethodSig.Params.Count == 2 && a.MethodSig.Params[1].ElementType == ElementType.Boolean),
			"JProperty" => module.Find("Newtonsoft.Json.Linq.JProperty", false),
			_ => throw new ArgumentException(member),
		};

		[Theory]
		[InlineData(true, "EditMethod", "JPropertyList.Count")]
		[InlineData(true, "EditMethod", "JToken.SelectTokens")]
		[InlineData(true, "EditClass", "JProperty")]
		[InlineData(false, "EditMethod", "JPropertyList.Count")]
		[InlineData(false, "EditClass", "JProperty")]
		public void NetStandardLibrary_EditWithoutChanges_CompilesAndKeepsNullability(bool netStandardReferenceAssembly, string editKind, string member) {
			var kind = Enum.Parse<EditKind>(editKind);
			var file = Path.Combine(SamplesDir, "Newtonsoft.Json.dll");
			var module = LoadNewtonsoft(netStandardReferenceAssembly);
			if (netStandardReferenceAssembly) {
				// (The case of the test: netstandard doesn't have a NullableAttribute, only the library has an internal one)
				var netstandard = module.Context.AssemblyResolver.Resolve(module.GetAssemblyRefs().Single(a => a.Name == "netstandard"), module);
				Assert.Equal(Path.Combine(SamplesDir, "ref", "netstandard.dll"), netstandard?.ManifestModule.Location);
				Assert.Null(netstandard!.ManifestModule.Find("System.Runtime.CompilerServices.NullableAttribute", false));
				Assert.DoesNotContain(netstandard.ManifestModule.ExportedTypes, a => a.FullName == "System.Runtime.CompilerServices.NullableAttribute");
			}

			var editedMember = Find(module, member);
			var result = EditPipeline.Decompile(LanguageRunner.Create(), kind, module, editedMember);
			Assert.True(EditPipeline.CompileAndImport(result, module, editedMember), result.Describe());

			var image = new MemoryStream();
			module.Write(image);
			var topLevelType = editedMember as TypeDef ?? editedMember.DeclaringType;
			while (topLevelType.DeclaringType is not null)
				topLevelType = topLevelType.DeclaringType;
			var differences = NullabilityOracle.Compare(File.ReadAllBytes(file), image.ToArray(), topLevelType.FullName);
			Assert.True(differences.Count == 0, string.Join(" | ", differences.Take(5)));
			// The edited code uses the library's own attributes, not a newer framework's
			Assert.DoesNotContain(module.GetTypeRefs(), a => a.Namespace == "System.Runtime.CompilerServices" && a.Name.String.StartsWith("Nullable", StringComparison.Ordinal));
		}
	}
}
