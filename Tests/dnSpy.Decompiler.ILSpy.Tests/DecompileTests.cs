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
using dnSpy.Decompiler.ILSpy.Core.Output;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler.TypeSystem;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	public class DecompileTests {
		readonly ITestOutputHelper log;

		public DecompileTests(ITestOutputHelper log) => this.log = log;

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void References_MapBackToTheRightDnlibDefinitions(bool normalize) {
			var module = TestCompiler.Compile(nameof(References_MapBackToTheRightDnlibDefinitions) + normalize, TestPrograms.ControlFlow).Load();
			var decompiler = TestDecompiler.Create(alwaysNormalize: normalize);
			var result = decompiler.DecompileType(module.Find("ControlFlow", false));

			int mapped = 0;
			var mismatches = new List<string>();
			foreach (var reference in result.References) {
				if (reference.Target is not IEntity entity)
					continue;
				var dnlib = decompiler.Cache.ToDnlib(entity);
				if (dnlib is null)
					continue; // defined in an assembly that isn't bridged or not a definition
				mapped++;
				var text = result.Text.Substring(reference.Start, reference.Length);
				if (!NameMatches(dnlib, text))
					mismatches.Add($"'{text}' -> {dnlib}");
			}
			log.WriteLine($"{mapped} references mapped to dnlib definitions");
			foreach (var m in mismatches)
				log.WriteLine("Mismatch: " + m);
			Assert.True(mapped > 20, $"Only {mapped} references were mapped");
			Assert.Empty(mismatches);
		}

		static readonly Dictionary<string, string> keywordTypes = new Dictionary<string, string> {
			["object"] = "System.Object", ["string"] = "System.String", ["bool"] = "System.Boolean", ["char"] = "System.Char",
			["sbyte"] = "System.SByte", ["byte"] = "System.Byte", ["short"] = "System.Int16", ["ushort"] = "System.UInt16",
			["int"] = "System.Int32", ["uint"] = "System.UInt32", ["long"] = "System.Int64", ["ulong"] = "System.UInt64",
			["float"] = "System.Single", ["double"] = "System.Double", ["decimal"] = "System.Decimal", ["void"] = "System.Void",
			["nint"] = "System.IntPtr", ["nuint"] = "System.UIntPtr",
		};

		/// <summary>
		/// Does the referenced dnlib definition match the text? Follows upstream's reference conventions: keywords
		/// reference their type, an invocation's parentheses reference the invoked method, local functions use their
		/// source name.
		/// </summary>
		static bool NameMatches(IMDTokenProvider dnlib, string text) {
			switch (dnlib) {
			case TypeDef type:
				// Nullable annotation (string?)
				if (text == "?")
					return true;
				if (keywordTypes.TryGetValue(text, out var fullName))
					return type.FullName == fullName;
				var name = type.Name.String;
				int tick = name.IndexOf('`');
				return (tick >= 0 ? name.Substring(0, tick) : name) == text;
			case MethodDef method:
				if (text is "(" or ")" or "[" or "]")
					return true;
				// Operators reference the method that implements them (string + string is String.Concat)
				if (text.Length <= 3 && !char.IsLetter(text[0]))
					return method.IsStatic;
				if (method.IsConstructor)
					return method.DeclaringType.Name.String.StartsWith(text) || text == "base" || text == "this";
				var methodName = method.Name.String;
				// Local function: <Outer>g__Name|N
				int g = methodName.IndexOf(">g__");
				if (g >= 0) {
					int bar = methodName.IndexOf('|', g);
					methodName = methodName.Substring(g + 4, (bar < 0 ? methodName.Length : bar) - (g + 4));
				}
				return methodName == text;
			case IMemberDef member:
				return member.Name.String == text;
			default:
				return true;
			}
		}

		/// <summary>
		/// A C# 12 inline array argument ('Path.Combine(a, b, c, d, e)' passes a ReadOnlySpan of the core library's
		/// InlineArray5) when the core library can't be resolved: upstream 11.2 leaves the argument out of the tree and
		/// its output throws (the whole type failed to decompile). DnSpyOutputVisitor writes a comment instead.
		/// </summary>
		[Fact]
		public void InlineArrayArgument_UnresolvedCoreLibrary_DoesNotFailTheType() {
			const string source = """
				public class Paths {
					public static string Combine(string a, string b, string c, string d, string e) => System.IO.Path.Combine(a, b, c, d, e);
					public static int Other() => 42;
				}
				""";
			var image = TestCompiler.Compile(nameof(InlineArrayArgument_UnresolvedCoreLibrary_DoesNotFailTheType), source).Image;
			Assert.Contains("InlineArray5", System.Text.Encoding.ASCII.GetString(image));
			// Nothing resolves (e.g. an assembly whose references aren't available)
			var module = ModuleDefMD.Load(image, new ModuleContext(NullResolver.Instance, NullResolver.Instance));
			var type = module.Find("Paths", false);
			var text = LanguageRunner.Decompile(LanguageRunner.Create(), type).Text;
			log.WriteLine(text);
			Assert.Contains("public static int Other()", text);
			Assert.Contains("Path.Combine(", text);
		}

		/// <summary>
		/// Upstream 11.2 moves a constructor's assignment to a field initializer when the parameter it reads is inside
		/// a C# conversion ('(int)(code - 4611)'), which doesn't compile and empties the constructor (Iced's MvexInfo).
		/// ConstructorParameterInitializerTransform moves it back.
		/// </summary>
		[Theory]
		[InlineData("struct")]
		[InlineData("sealed class")]
		public void ConstructorAssignmentReadingAParameter_StaysInTheConstructor(string kind) {
			string source = $$"""
				public enum Code { A = 4611, B, C }
				public readonly {{(kind == "struct" ? "struct" : "class")}} Info {
					public int Other() => 1;
					readonly int index;
					public Info(Code code) => index = (int)code - 4611;
					public int Index => index;
				}
				public static class Program {
					public static int Run() => new Info(Code.C).Index;
				}
				""";
			if (kind != "struct")
				source = source.Replace("public readonly class", "public sealed class");
			var compiled = TestCompiler.Compile(nameof(ConstructorAssignmentReadingAParameter_StaysInTheConstructor) + kind.Replace(" ", ""), source);
			var module = compiled.Load();
			var text = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("Info", false)).Text;
			log.WriteLine(text);
			Assert.DoesNotContain("int index = ", text);
			Assert.Matches(@"public Info\(Code code\)\s*\{\s*index = \(int\)\(code - 4611\);\s*\}", text);
			// Same code as the compiler's: it compiles and runs the same
			var roundTrip = TestCompiler.Compile(nameof(ConstructorAssignmentReadingAParameter_StaysInTheConstructor) + kind.Replace(" ", "") + "2",
				"public enum Code { A = 4611, B, C }\n" + text + "\npublic static class Program { public static int Run() => new Info(Code.C).Index; }").Load();
			Assert.NotNull(roundTrip.Find("Info", false));
		}

		/// <summary>
		/// 'await' in a 'finally' (Microsoft.Extensions.Hosting's RunAsync). The compiler rewrites the finally into a
		/// catch of 'object' that saves the exception and rethrows it after the finally's code. dnSpy's old engine (its
		/// fork of ILSpy 2.x) showed that rewritten code: an empty 'catch (object obj)' that doesn't compile, and the
		/// rethrow. Upstream turns it back into try/finally.
		/// </summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void AwaitInFinally_IsATryFinally(bool optimize) {
			const string source = """
				using System;
				using System.Threading;
				using System.Threading.Tasks;
				public interface IHost : IDisposable {
					Task StartAsync(CancellationToken token);
					Task StopAsync(CancellationToken token);
				}
				public static class HostExtensions {
					public static Task WaitForShutdownAsync(this IHost host, CancellationToken token) => Task.CompletedTask;
					public static async Task RunAsync(this IHost host, CancellationToken token = default(CancellationToken)) {
						try {
							await host.StartAsync(token).ConfigureAwait(continueOnCapturedContext: false);
							await host.WaitForShutdownAsync(token).ConfigureAwait(continueOnCapturedContext: false);
						}
						finally {
							if (host is IAsyncDisposable asyncDisposable) {
								await asyncDisposable.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
							}
							else {
								host.Dispose();
							}
						}
					}
				}
				""";
			var module = TestCompiler.Compile(nameof(AwaitInFinally_IsATryFinally) + optimize, source, optimize).Load();
			var text = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("HostExtensions", false)).Text;
			log.WriteLine(text);
			Assert.DoesNotContain("catch", text);
			Assert.DoesNotContain("ExceptionDispatchInfo", text);
			Assert.Matches(@"try\s*\{\s*await host\.StartAsync\(token\)\.ConfigureAwait\(continueOnCapturedContext: false\);\s*" +
				@"await host\.WaitForShutdownAsync\(token\)\.ConfigureAwait\(continueOnCapturedContext: false\);\s*\}\s*finally\s*\{\s*" +
				@"if \(host is IAsyncDisposable asyncDisposable\)\s*\{\s*await asyncDisposable\.DisposeAsync\(\)\.ConfigureAwait\(continueOnCapturedContext: false\);\s*\}\s*" +
				@"else\s*\{\s*host\.Dispose\(\);\s*\}\s*\}", text);
		}

		/// <summary>
		/// Compiler-generated types (shown in the tree with "show hidden compiler generated types and members") are
		/// decompiled: upstream's DecompileTypes() skips them
		/// </summary>
		[Fact]
		public void HiddenCompilerGeneratedTypes_AreDecompiled() {
			var module = TestCompiler.Compile(nameof(HiddenCompilerGeneratedTypes_AreDecompiled), TestPrograms.StateMachines).Load();
			var hidden = module.GetTypes().Where(a => a.IsNested && a.Name.String.StartsWith("<", System.StringComparison.Ordinal)).ToList();
			Assert.True(hidden.Count >= 3, hidden.Count.ToString());
			var language = LanguageRunner.Create();
			foreach (var type in hidden) {
				var text = LanguageRunner.Decompile(language, type).Text;
				Assert.True(text.Contains("class ", System.StringComparison.Ordinal) || text.Contains("struct ", System.StringComparison.Ordinal), type.FullName + ":\n" + text);
				Assert.Contains(type.Name.String, text);
			}
		}

		[Fact]
		public void ModernCSharp_IsDecompiledWithModernSyntax() {
			var module = TestCompiler.Compile(nameof(ModernCSharp_IsDecompiledWithModernSyntax), TestPrograms.ModernCSharp).Load();
			var decompiler = TestDecompiler.Create();
			var text = string.Join("\n", module.Types.Where(t => t.Namespace == "Modern").Select(t => decompiler.DecompileType(t).Text));
			log.WriteLine(text);

			Assert.Contains("class Point(int x, int y)", text);           // C# 12 primary constructor
			Assert.Contains("extension(string s)", text);                 // C# 14 extension block
			Assert.Contains("field = value.Trim();", text);               // C# 14 field keyword
			Assert.Contains("required string Name", text);                // C# 11 required member
			Assert.Contains("record Person(string First, string Last)", text);
			Assert.Contains("[InlineArray(4)]", text);                    // C# 12 inline array
			Assert.Contains("public ref int Value;", text);               // C# 11 ref field
			Assert.Contains("\"hello\"u8", text);                         // C# 11 UTF-8 literal
		}
	}
}
