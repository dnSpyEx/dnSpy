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
using System.Reflection;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// dnSpy's edit code commands without changing the code: decompile the documents the edit dialog shows, compile
	/// them, import the result into the module and run the program. It must compile, import and behave the same.
	/// </summary>
	public class EditRoundTripTests {
		readonly ITestOutputHelper log;
		public EditRoundTripTests(ITestOutputHelper log) => this.log = log;

		const string Upstream = "upstream";
		// Upstream, and the code that the edit doesn't need is always left out of the decompiled image (dnSpy only
		// does it for big types)
		const string UpstreamStubs = "upstream-stubs";

		static IDecompiler GetDecompiler(string engine) {
			var language = LanguageRunner.Create();
			if (engine == UpstreamStubs)
				language.EditUnusedBodiesMinSize = 0;
			return language;
		}

		static (string name, string source, string ns) GetProgram(string program) => program switch {
			"Classic" => ("EditClassic", EditPrograms.Classic, "EditClassic"),
			"Modern" => ("EditModern", EditPrograms.Modern, "EditModern"),
			"Compat" => ("EditCompat", EditPrograms.Compat, "EditCompat"),
			_ => throw new ArgumentException(program),
		};

		static readonly Dictionary<(string, bool), CompiledProgram> compiledPrograms = new Dictionary<(string, bool), CompiledProgram>();
		static CompiledProgram Compile(string program, bool optimize) {
			lock (compiledPrograms) {
				if (!compiledPrograms.TryGetValue((program, optimize), out var compiled)) {
					var (name, source, _) = GetProgram(program);
					compiledPrograms[(program, optimize)] = compiled = TestCompiler.Compile(name + (optimize ? "R" : "D"), source, optimize);
				}
				return compiled;
			}
		}

		static string RunImage(byte[] image, string ns) => ProgramRunner.Run(image, ns);

		internal sealed class Outcome {
			public string Description = "";
			public string Category = "";
			public EditResult? Result;
		}

		/// <summary>
		/// Runs one edit on a fresh copy of the module; null if everything worked
		/// </summary>
		internal static Outcome? RunEdit(IDecompiler decompiler, EditKind kind, CompiledProgram compiled, string ns, string expected, uint token, Action<EditResult, ModuleDef, IMemberDef?>? change = null, Func<ModuleDef, string?>? check = null) {
			try {
				return RunEditCore(decompiler, kind, compiled, ns, expected, token, change, check);
			}
			finally {
				// Each edit uses a new copy of the module: free the decompiler's images and type systems of the old ones
				(decompiler as UpstreamCSharpLanguage)?.OnAllModulesRemoved();
			}
		}

		static Outcome? RunEditCore(IDecompiler decompiler, EditKind kind, CompiledProgram compiled, string ns, string expected, uint token, Action<EditResult, ModuleDef, IMemberDef?>? change, Func<ModuleDef, string?>? check) {
			var module = compiled.Load();
			var member = token == 0 ? null : (IMemberDef)module.ResolveToken(token);
			var result = EditPipeline.Decompile(decompiler, kind, module, member);
			if (result.DecompileError is not null)
				return new Outcome { Category = "decompile", Description = result.Describe(), Result = result };
			change?.Invoke(result, module, member);
			if (!EditPipeline.CompileAndImport(result, module, member)) {
				return new Outcome {
					Category = result.CompileErrors.Count != 0 ? "compile" : "import",
					Description = result.Describe(),
					Result = result,
				};
			}
			byte[] image;
			try {
				var ms = new MemoryStream();
				module.Write(ms);
				image = ms.ToArray();
			}
			catch (Exception ex) {
				return new Outcome { Category = "write", Description = $"{kind} {result.Target}: {ex.Message}", Result = result };
			}
			var actual = RunImage(image, ns);
			if (actual != expected) {
				var exp = expected.Split(Environment.NewLine);
				var act = actual.Split(Environment.NewLine);
				var diff = string.Join(" / ", Enumerable.Range(0, Math.Max(exp.Length, act.Length))
					.Where(i => i >= exp.Length || i >= act.Length || exp[i] != act[i])
					.Take(3)
					.Select(i => $"expected `{(i < exp.Length ? exp[i] : "")}` got `{(i < act.Length ? act[i] : "")}`"));
				return new Outcome { Category = "behavior", Description = $"{kind} {result.Target}: {diff}", Result = result };
			}
			if (check?.Invoke(module) is string problem)
				return new Outcome { Category = "check", Description = $"{kind} {result.Target}: {problem}", Result = result };
			// The members keep their nullable annotations ([NullableContext], [Nullable])
			if (NullabilityOracle.Compare(compiled.Image, image) is { Count: > 0 } nullability)
				return new Outcome { Category = "nullability", Description = $"{kind} {result.Target}: " + string.Join(" | ", nullability.Take(3)), Result = result };
			// The edit dialog puts the caret on the edited member
			if ((kind == EditKind.EditMethod || kind == EditKind.EditClass) && result.Documents[0].Caret is null && !HasNoDefinition(member, result.Documents[0].Text))
				return new Outcome { Category = "caret", Description = $"{kind} {result.Target}: the edited member isn't in the main document", Result = result };
			return null;
		}

		/// <summary>
		/// Methods without a declaration in C#: what the compiler adds to a record, the accessors of a field-like event
		/// and a primary constructor (the type's parameter list)
		/// </summary>
		static bool HasNoDefinition(IMemberDef? member, string text) {
			if (member is not MethodDef method)
				return false;
			bool compilerGenerated = method.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute");
			if (compilerGenerated && method.DeclaringType.FindMethod("PrintMembers") is not null)
				return true;
			if (compilerGenerated && (method.IsAddOn || method.IsRemoveOn))
				return true;
			return method.IsInstanceConstructor && method.Parameters.Count > 1 &&
				System.Text.RegularExpressions.Regex.IsMatch(text, @"\b(class|struct|record)\b[^\n{;]*\b" + System.Text.RegularExpressions.Regex.Escape(StripArity(method.DeclaringType.Name)) + @"\s*\(");
		}

		/// <summary>
		/// The members the tree view shows (the commands are on their nodes)
		/// </summary>
		internal static IEnumerable<MethodDef> GetEditableMethods(IDecompiler decompiler, ModuleDef module) {
			foreach (var type in module.GetTypes()) {
				if (type.IsGlobalModuleType || !IsVisible(decompiler, type))
					continue;
				foreach (var method in type.Methods) {
					if (method.HasBody && decompiler.ShowMember(method))
						yield return method;
				}
			}
		}

		static bool IsVisible(IDecompiler decompiler, TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (!decompiler.ShowMember(t))
					return false;
			}
			return true;
		}

		internal static IEnumerable<TypeDef> GetEditableTypes(IDecompiler decompiler, ModuleDef module) =>
			module.Types.Where(t => !t.IsGlobalModuleType && decompiler.ShowMember(t));

		void Report(string title, int total, List<Outcome> failures) {
			log.WriteLine($"{title}: {total - failures.Count}/{total} OK" + (failures.Count == 0 ? "" : ", failures: " +
				string.Join(", ", failures.GroupBy(a => a.Category).Select(g => $"{g.Key} {g.Count()}"))));
			foreach (var f in failures)
				log.WriteLine("  " + f.Description);
		}

		void Dump(Outcome outcome) {
			if (outcome.Result is null)
				return;
			foreach (var doc in outcome.Result.Documents) {
				log.WriteLine($"  ---- {doc.Name}");
				log.WriteLine(doc.Text);
			}
		}

		static readonly bool dumpFailures = Environment.GetEnvironmentVariable("EDIT_DUMP") == "1";
		static readonly string? onlyTarget = Environment.GetEnvironmentVariable("EDIT_ONLY");

		List<Outcome> EditAll(string program, bool optimize, string engine, EditKind kind) {
			if (Environment.GetEnvironmentVariable("EDIT_ENGINE") is string onlyEngine && onlyEngine != engine)
				return new List<Outcome>();
			if (Environment.GetEnvironmentVariable("EDIT_PROGRAM") is string onlyProgram && onlyProgram != program + (optimize ? "R" : "D"))
				return new List<Outcome>();
			var decompiler = GetDecompiler(engine);
			var compiled = Compile(program, optimize);
			var (_, _, ns) = GetProgram(program);
			var expected = RunImage(compiled.Image, ns);
			Assert.False(expected.StartsWith("EXCEPTION") || expected.StartsWith("CRASH") || expected == "TIMEOUT", expected);
			var module = compiled.Load();
			var targets = new List<(uint token, string name)>();
			switch (kind) {
			case EditKind.EditMethod:
				targets.AddRange(GetEditableMethods(decompiler, module).Select(m => (m.MDToken.Raw, m.FullName)));
				break;
			case EditKind.EditClass:
			case EditKind.AddMembers:
				targets.AddRange(GetEditableTypes(decompiler, module).Where(t => kind != EditKind.AddMembers || (!t.IsEnum && !t.IsDelegate)).Select(t => (t.MDToken.Raw, t.FullName)));
				break;
			case EditKind.EditAssembly:
				targets.Add((0, module.Name));
				break;
			}
			if (onlyTarget is not null)
				targets.RemoveAll(a => !a.name.Contains(onlyTarget, StringComparison.Ordinal));
			var failures = new List<Outcome>();
			foreach (var (token, targetName) in targets) {
				if (Environment.GetEnvironmentVariable("EDIT_TRACE") is string traceFile)
					File.AppendAllText(traceFile, $"{engine} {program} {optimize} {kind} {targetName}\n");
				var outcome = kind switch {
					EditKind.AddMembers => RunEdit(decompiler, kind, compiled, ns, expected, token, AddMethod, CheckAddedMethod(token)),
					EditKind.EditAssembly => RunEdit(decompiler, kind, compiled, ns, expected, token, check: CheckAssemblyAttributes(compiled)),
					_ => RunEdit(decompiler, kind, compiled, ns, expected, token),
				};
				if (outcome is not null) {
					failures.Add(outcome);
					if (dumpFailures)
						Dump(outcome);
				}
			}
			Report($"{engine} {program} {(optimize ? "Release" : "Debug")} {kind}", targets.Count, failures);
			if (engine == UpstreamStubs && targets.Count != 0)
				Assert.True(((UpstreamCSharpLanguage)decompiler).ImagesWithoutUnusedBodies > 0, "The decompiled image always had all the code");
			return failures;
		}

		const string AddedMethodName = "AddedByTest";

		/// <summary>
		/// Adds a method to the edited type (the user's new code)
		/// </summary>
		static void AddMethod(EditResult result, ModuleDef module, IMemberDef? member) {
			var type = (TypeDef)member!;
			var doc = result.Documents[0];
			var tree = CSharpSyntaxTree.ParseText(doc.Text);
			var decl = tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().FirstOrDefault(a => a.Identifier.ValueText == StripArity(type.Name));
			if (decl is null)
				return;
			var newMember = $" public static int {AddedMethodName}() => 42; ";
			if (decl.OpenBraceToken.Span.Length == 0) {
				// 'record R(int A);': the user adds a body
				var semicolon = decl.SemicolonToken.Span;
				doc.Text = doc.Text.Substring(0, semicolon.Start) + " {" + newMember + "}" + doc.Text.Substring(semicolon.End);
				return;
			}
			int pos = decl.OpenBraceToken.Span.End;
			doc.Text = doc.Text.Substring(0, pos) + newMember + doc.Text.Substring(pos);
		}

		static string StripArity(string name) {
			int tick = name.IndexOf('`');
			return tick < 0 ? name : name.Substring(0, tick);
		}

		static Func<ModuleDef, string?> CheckAddedMethod(uint token) => module => {
			var type = (TypeDef)module.ResolveToken(token);
			return type.FindMethod(AddedMethodName) is null ? "the new method wasn't added" : null;
		};

		/// <summary>
		/// The edited module has the same assembly and module attributes and declarative security
		/// </summary>
		static Func<ModuleDef, string?> CheckAssemblyAttributes(CompiledProgram compiled) => module => {
			var original = compiled.Load();
			var expected = DescribeAttributes(original);
			var actual = DescribeAttributes(module);
			if (expected.SequenceEqual(actual))
				return null;
			return "attributes differ: missing " + string.Join(", ", expected.Except(actual)) + "; added " + string.Join(", ", actual.Except(expected));
		};

		static List<string> DescribeAttributes(ModuleDef module) {
			var list = new List<string>();
			foreach (var ca in module.Assembly!.CustomAttributes)
				list.Add("assembly: " + Describe(ca));
			foreach (var ca in module.CustomAttributes)
				list.Add("module: " + Describe(ca));
			foreach (var ds in module.Assembly.DeclSecurities)
				list.Add($"security: {ds.Action} " + string.Join(", ", ds.SecurityAttributes.Select(a => a.TypeFullName + "(" + string.Join(", ", a.NamedArguments.Select(n => n.Name + "=" + n.Value)) + ")")));
			list.Sort(StringComparer.Ordinal);
			return list;
		}

		static string Describe(CustomAttribute ca) =>
			ca.TypeFullName + "(" + string.Join(", ", ca.ConstructorArguments.Select(a => a.Value?.ToString())) + "; " +
			string.Join(", ", ca.NamedArguments.Select(n => n.Name + "=" + n.Value)) + ")";

		/// <summary>
		/// Code upstream can't decompile to C# that compiles (it also shows it that way). Edits of other code work.
		/// </summary>
		static readonly (string target, bool exact, string why)[] knownUpstreamFailures = {
			("__Secret", false, "file-local types (C# 11) have their metadata name"),
			("FileUser", false, "references to file-local types have their metadata name"),
			("<>f__AnonymousDelegate", false, "the compiler's delegate types of lambdas with default or params parameters"),
			("Features::Lambdas", false, "lambdas with default or params parameters are given compiler-generated delegate types"),
			// (Edit Class: the whole type, it has Lambdas())
			("EditModern.Features", true, "lambdas with default or params parameters are given compiler-generated delegate types"),
		};

		static void AssertNoUnexpectedFailures(List<Outcome> failures) {
			var unexpected = failures.Where(f => !knownUpstreamFailures.Any(k => f.Result?.Target is string target &&
				(k.exact ? target == k.target : target.Contains(k.target, StringComparison.Ordinal)))).Select(a => a.Description).ToList();
			Assert.Empty(unexpected);
		}

		/// <summary>
		/// Inserts a statement at the start (or end) of a method or constructor's body
		/// </summary>
		static string InsertStatement(string code, string typeName, string memberName, string statement, bool atEnd) {
			var root = CSharpSyntaxTree.ParseText(code).GetRoot();
			var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>().First(a => a.Identifier.ValueText == typeName);
			BlockSyntax? body = memberName switch {
				".ctor" => type.Members.OfType<ConstructorDeclarationSyntax>().First(a => !a.Modifiers.Any(m => m.ValueText == "static") && a.ParameterList.Parameters.Count > 0).Body,
				".cctor" => type.Members.OfType<ConstructorDeclarationSyntax>().First(a => a.Modifiers.Any(m => m.ValueText == "static")).Body,
				_ => type.Members.OfType<MethodDeclarationSyntax>().First(a => a.Identifier.ValueText == memberName).Body,
			};
			if (body is null)
				throw new InvalidOperationException($"{typeName}.{memberName} has no block body:\n{code}");
			int pos = atEnd ? body.CloseBraceToken.SpanStart : body.OpenBraceToken.Span.End;
			return code.Substring(0, pos) + " " + statement + " " + code.Substring(pos);
		}

		public static IEnumerable<object[]> ChangeCases() {
			foreach (var engine in new[] { Upstream, UpstreamStubs }) {
				foreach (var optimize in new[] { true, false }) {
					// A new await (new state machine code), a new yield return, a new lambda, a constructor and a static
					// constructor's code
					yield return new object[] { engine, optimize, "Algorithms", "SumAsync", "await Task.Yield(); values = values.Append(1000).ToArray();", false };
					yield return new object[] { engine, optimize, "Algorithms", "Fibonacci", "yield return 100;", false };
					yield return new object[] { engine, optimize, "Algorithms", "Closures", "Func<int, int> twice = v => v * 2 + n; n = twice(n);", false };
					yield return new object[] { engine, optimize, "Rect", ".ctor", "width += 1;", false };
					yield return new object[] { engine, optimize, "Counter", ".cctor", "registry[\"extra\"] = 2;", true };
				}
			}
		}

		/// <summary>
		/// Edit Method with a change: the edited program behaves like the program compiled from the changed source
		/// </summary>
		[Theory]
		[MemberData(nameof(ChangeCases))]
		public void EditMethod_WithChanges_BehavesLikeTheChangedSource(string engine, bool optimize, string typeName, string memberName, string statement, bool atEnd) {
			var decompiler = GetDecompiler(engine);
			var compiled = Compile("Classic", optimize);
			var (name, source, ns) = GetProgram("Classic");
			var changed = TestCompiler.Compile(name + "Changed" + (optimize ? "R" : "D"), InsertStatement(source, typeName, memberName, statement, atEnd), optimize);
			var expected = RunImage(changed.Image, ns);
			Assert.NotEqual(RunImage(compiled.Image, ns), expected);
			var module = compiled.Load();
			var type = module.GetTypes().First(a => a.Name == typeName);
			var method = memberName switch {
				".ctor" => type.Methods.First(a => a.IsInstanceConstructor && a.Parameters.Count > 1),
				".cctor" => type.Methods.First(a => a.IsStaticConstructor),
				_ => type.Methods.First(a => a.Name == memberName),
			};
			var token = method.MDToken.Raw;
			var outcome = RunEdit(decompiler, EditKind.EditMethod, compiled, ns, expected, token, (result, m, member) => {
				var main = result.Documents[0];
				main.Text = InsertStatement(main.Text, typeName, memberName, statement, atEnd);
				// Without the code of the other members, only the namespaces that the edited member and the signatures
				// use are imported: the user adds the using of the new code
				if (engine == UpstreamStubs && statement.Contains(".ToArray()", StringComparison.Ordinal))
					main.Text = "using System.Linq;" + Environment.NewLine + main.Text;
			});
			if (outcome is not null && dumpFailures)
				Dump(outcome);
			log.WriteLine($"{engine} {typeName}.{memberName}: {outcome?.Description ?? "OK"}");
			Assert.Null(outcome?.Description);
		}

		static IEnumerable<object[]> GetCases(params string[] engines) {
			foreach (var engine in engines) {
				foreach (var program in new[] { "Classic", "Modern" }) {
					foreach (var optimize in new[] { true, false })
						yield return new object[] { engine, program, optimize };
				}
			}
		}

		// Edit Method and Add Class Members leave code out of the decompiled image, Edit Class and Edit Assembly
		// Attributes don't
		public static IEnumerable<object[]> Cases() => GetCases(Upstream, UpstreamStubs);
		public static IEnumerable<object[]> CasesWithAllCode() => GetCases(Upstream);

		[Theory]
		[MemberData(nameof(Cases))]
		public void EditMethod_WithoutChanges_CompilesAndBehavesTheSame(string engine, string program, bool optimize) {
			var failures = EditAll(program, optimize, engine, EditKind.EditMethod);
			AssertNoUnexpectedFailures(failures);
		}

		[Theory]
		[MemberData(nameof(CasesWithAllCode))]
		public void EditClass_WithoutChanges_CompilesAndBehavesTheSame(string engine, string program, bool optimize) {
			var failures = EditAll(program, optimize, engine, EditKind.EditClass);
			AssertNoUnexpectedFailures(failures);
		}

		[Theory]
		[MemberData(nameof(Cases))]
		public void AddMembers_AddsTheNewMethod(string engine, string program, bool optimize) {
			var failures = EditAll(program, optimize, engine, EditKind.AddMembers);
			AssertNoUnexpectedFailures(failures);
		}

		/// <summary>
		/// A library as compiled for an older framework (<see cref="EditPrograms.Compat"/>): edited code binds to the
		/// library's own copies of newer types (not an ambiguity, CS0433) and to the attribute a source generator added
		/// (CS0246), and may call [Experimental] APIs. The saved module still references the library's own types.
		/// </summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void EditCompat_OwnTypes_GeneratorAttributes_ExperimentalApis(bool optimize) {
			var decompiler = GetDecompiler(Upstream);
			var compiled = Compile("Compat", optimize);
			var expected = RunImage(compiled.Image, "EditCompat");
			Assert.False(expected.StartsWith("EXCEPTION") || expected.StartsWith("CRASH"), expected);
			var module = compiled.Load();
			var targets = new List<(EditKind kind, uint token, string name)>();
			// (Not the generator's attribute class itself: the code would need [Embedded])
			foreach (var type in module.Types.Where(a => a.Namespace == "EditCompat" && a.Name != "GeneratedMarkerAttribute")) {
				targets.Add((EditKind.EditClass, type.MDToken.Raw, type.FullName));
				foreach (var method in type.Methods.Where(a => a.HasBody && decompiler.ShowMember(a)))
					targets.Add((EditKind.EditMethod, method.MDToken.Raw, method.FullName));
			}
			var failures = new List<string>();
			foreach (var (kind, token, name) in targets) {
				var outcome = RunEdit(decompiler, kind, compiled, "EditCompat", expected, token, check: CheckOwnTypesReferenced);
				if (outcome is not null) {
					failures.Add(outcome.Description);
					if (dumpFailures)
						Dump(outcome);
				}
			}
			Report($"upstream Compat {(optimize ? "Release" : "Debug")}", targets.Count, failures.Select(a => new Outcome { Description = a, Category = "fail" }).ToList());
			Assert.Empty(failures);
		}

		static readonly string[] compatOwnTypes = {
			"System.Diagnostics.CodeAnalysis.NotNullWhenAttribute",
			"System.Runtime.CompilerServices.IsExternalInit",
			"System.Index",
			"EditCompat.GeneratedMarkerAttribute",
		};

		static string? CheckOwnTypesReferenced(ModuleDef module) {
			var refs = module.GetTypeRefs().Where(a => compatOwnTypes.Contains(a.FullName)).Select(a => a.FullName + " in " + a.DefinitionAssembly?.Name).ToList();
			return refs.Count == 0 ? null : "references another assembly's copy: " + string.Join(", ", refs);
		}

		const string OldCSharpProgram = """
			[assembly: System.Reflection.AssemblyDescription("old")]
			namespace EditOld {
				public static class Program {
					public static string Run() => "old";
				}
			}
			""";

		/// <summary>
		/// Edit Assembly Attributes on an assembly compiled by an older compiler (no [module: RefSafetyRules(11)]): the
		/// compiler adds the attributes of the code it compiles, the assembly keeps its own
		/// </summary>
		[Fact]
		public void EditAssembly_OlderCompiler_KeepsTheCompilerAttributes() {
			// (Roslyn always adds it for .NET 7+ references, so remove it)
			const string refSafetyRules = "System.Runtime.CompilerServices.RefSafetyRulesAttribute";
			var original = TestCompiler.Compile("EditOld", OldCSharpProgram).Load();
			Assert.True(original.CustomAttributes.IsDefined(refSafetyRules));
			original.CustomAttributes.RemoveAll(refSafetyRules);
			var image = new MemoryStream();
			original.Write(image);
			var compiled = new CompiledProgram("EditOld", image.ToArray(), Array.Empty<byte>(), optimized: false);
			Assert.DoesNotContain(compiled.Load().CustomAttributes, a => a.TypeFullName == refSafetyRules);
			var outcome = RunEdit(GetDecompiler(Upstream), EditKind.EditAssembly, compiled, "EditOld", RunImage(compiled.Image, "EditOld"), 0, check: CheckAssemblyAttributes(compiled));
			Assert.Null(outcome?.Description);
		}

		const string SelfReferencingProgram = """
			using System.Collections;
			using System.Collections.Generic;
			namespace EditSelf {
				public interface INode<out T> : IEnumerable<T> where T : Node { }
				public class Node : INode<Node> {
					public int Value;
					public IEnumerator<Node> GetEnumerator() { yield return this; }
					IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
					public int Twice() => Value * 2;
				}
				public static class Program {
					public static string Run() => new Node { Value = 21 }.Twice().ToString();
				}
			}
			""";

		/// <summary>
		/// A module that references older versions of assemblies than the compiler gets (e.g. a netstandard 2.0 library
		/// on .NET 10): Roslyn recursed until the process died (stack overflow) on 'class Node : INode&lt;Node&gt;'. (If this
		/// fails, the test host crashes.)
		/// </summary>
		[Fact]
		public void EditMethod_ModuleReferencingOlderAssemblies_Compiles() {
			var module = TestCompiler.Compile("EditSelf", SelfReferencingProgram).Load();
			foreach (var asmRef in module.GetAssemblyRefs())
				asmRef.Version = new Version(4, 0, 0, 0);
			var options = new dnlib.DotNet.Writer.ModuleWriterOptions(module);
			options.MetadataOptions.Flags |= dnlib.DotNet.Writer.MetadataFlags.PreserveAll;
			var image = new MemoryStream();
			module.Write(image, options);
			var compiled = new CompiledProgram("EditSelf", image.ToArray(), Array.Empty<byte>(), optimized: false);
			var token = compiled.Load().GetTypes().First(a => a.Name == "Node").FindMethod("Twice").MDToken.Raw;
			var outcome = RunEdit(GetDecompiler(Upstream), EditKind.EditMethod, compiled, "EditSelf", RunImage(compiled.Image, "EditSelf"), token);
			Assert.Null(outcome?.Description);
		}

		/// <summary>
		/// New code that uses a primary constructor parameter that the type doesn't store (Account's 'balance' only
		/// initializes a property): the compiler stores it in a new field that only the constructor, which isn't
		/// imported, sets. The import must fail instead of the parameter always being 0.
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void NewPrimaryConstructorCapture_IsAnImportError(bool addMembers) {
			var compiled = Compile("Modern", optimize: false);
			var type = compiled.Load().GetTypes().First(a => a.Name == "Account");
			var token = addMembers ? type.MDToken.Raw : type.FindMethod("Deposit").MDToken.Raw;
			var outcome = RunEdit(GetDecompiler(Upstream), addMembers ? EditKind.AddMembers : EditKind.EditMethod, compiled, "EditModern", "", token, (result, module, member) => {
				var main = result.Documents[0];
				if (addMembers) {
					var decl = CSharpSyntaxTree.ParseText(main.Text).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>().First(a => a.Identifier.ValueText == "Account");
					int pos = decl.OpenBraceToken.Span.End;
					main.Text = main.Text.Substring(0, pos) + " public decimal Initial() => balance; " + main.Text.Substring(pos);
				}
				else
					main.Text = InsertStatement(main.Text, "Account", "Deposit", "amount += balance * 0;", atEnd: false);
			});
			if (outcome is not null && dumpFailures)
				Dump(outcome);
			Assert.NotNull(outcome);
			Assert.Equal("import", outcome!.Category);
			Assert.Contains("IM0011", outcome.Description, StringComparison.Ordinal);
		}

		[Theory]
		[MemberData(nameof(CasesWithAllCode))]
		public void EditAssembly_WithoutChanges_KeepsTheAttributes(string engine, string program, bool optimize) {
			var failures = EditAll(program, optimize, engine, EditKind.EditAssembly);
			AssertNoUnexpectedFailures(failures);
		}
	}
}
