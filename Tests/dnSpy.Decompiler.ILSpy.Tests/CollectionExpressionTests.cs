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
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// C# 12 collection expressions (CollectionExpressionTransform): the decompiled code has '[a, .. b]' instead of
	/// the compiler's types and the code that fills lists, arrays and spans, it compiles, and it behaves the same
	/// (including the types of the collections it creates)
	/// </summary>
	public class CollectionExpressionTests {
		readonly ITestOutputHelper log;
		public CollectionExpressionTests(ITestOutputHelper log) => this.log = log;

		/// <summary>
		/// Interface targets (the compiler's own types), in all kinds of positions
		/// </summary>
		internal const string Interfaces = """
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using System.Text;

			namespace Collections {
				public static class Program {
					static IEnumerable<int> Enumerable(int a, int b) => [a, b];
					static IReadOnlyCollection<int> Collection(int a) => [a];
					static IReadOnlyList<string> List(string a, string b, string c) => [a, b, c];
					static IEnumerable<int> Empty() => [];
					static IEnumerable<int> SpreadArray(int[] xs, int y) => [.. xs, y];
					static IReadOnlyList<int> SpreadEnumerable(IEnumerable<int> xs) => [.. xs, 1, .. xs];
					static IReadOnlyList<int> SpreadList(List<int> xs) => [.. xs];
					static IReadOnlyCollection<int> SpreadSpan(ReadOnlySpan<int> xs) => [0, .. xs];
					static int Sum(IEnumerable<int> e) => e.Sum();
					static int Sum(IReadOnlyList<int> l, int extra) => l.Sum() + extra;
					static int Count(IReadOnlyCollection<int> c) => c.Count;
					static int Overloaded(IEnumerable<int> e) => 1;
					static int Overloaded(int[] a) => 2;
					static int Generic<T>(IEnumerable<T> e) => e.Count();
					static readonly IReadOnlyList<int> field = [1, 2, 3];
					static IReadOnlyList<int> Property { get; } = [4, 5];
					static IEnumerable<int> Iterator(int a) {
						foreach (var x in (IEnumerable<int>)[a, a + 1])
							yield return x;
					}

					sealed class Holder(IEnumerable<string> names) {
						public IReadOnlyList<string> Names { get; } = [.. names, "end"];
						public IReadOnlyCollection<int> Values = [1];
					}

					public static string Run() {
						var sb = new StringBuilder();
						void P(IEnumerable<int> e) => sb.Append(string.Join(",", e)).Append(';');
						P(Enumerable(1, 2));
						P(Collection(3));
						sb.Append(string.Join(",", List("a", "b", "c"))).Append(';');
						P(Empty());
						P(SpreadArray(new[] { 4, 5 }, 6));
						P(SpreadEnumerable(new[] { 7, 8 }.Where(x => x > 7)));
						P(SpreadList(new List<int> { 9, 10 }));
						P(SpreadSpan(new[] { 11, 12 }));
						IReadOnlyList<int> local = [13, 14];
						P(local);
						IEnumerable<int> one = [15];
						P(one);
						sb.Append(Sum([1, 2, 3])).Append(';');
						sb.Append(Sum([1, 2], 3)).Append(';');
						sb.Append(Count([1, 2, 3, 4])).Append(';');
						sb.Append(Overloaded((IEnumerable<int>)[1, 2])).Append(';');
						sb.Append(Generic<int>([1, 2, 3])).Append(';');
						sb.Append(((IReadOnlyList<int>)[1, 2]).Count).Append(';');
						Func<IEnumerable<int>> f = () => [16, 17];
						P(f());
						P(new[] { 18, 19 }.SelectMany(x => (IEnumerable<int>)[x, x]));
						IReadOnlyList<IReadOnlyList<int>> nested = [[20], [21, 22]];
						foreach (var n in nested)
							P(n);
						IEnumerable<int> t = sb.Length > 1000 ? [23] : [24, 25];
						P(t);
						P(field);
						P(Property);
						P(Iterator(26));
						var holder = new Holder(["x", "y"]);
						sb.Append(string.Join(",", holder.Names)).Append(';');
						P(holder.Values);
						sb.Append(local.GetType().Name).Append(';');
						sb.Append(one.GetType().Name).Append(';');
						sb.Append(SpreadList(new List<int> { 1 }).GetType().Name).Append(';');
						sb.Append(local is int[]).Append(';');
						return sb.ToString();
					}
				}
			}
			""";

		/// <summary>
		/// Lists, arrays, sets, immutable arrays, spans and params spans, with spreads of arrays, lists, collections
		/// and enumerables
		/// </summary>
		internal const string OtherTargets = """
			using System;
			using System.Collections.Generic;
			using System.Collections.Immutable;
			using System.Linq;
			using System.Text;

			namespace Collections2 {
				public static class Program {
					static List<int> ListOf(int a, int b) => [a, b, 3];
					static List<int> ListSpread(IEnumerable<int> xs, int[] ys) => [.. xs, 1, .. ys];
					static int[] ArrayOf(int a) => [a, 2];
					static int[] ArraySpread(int[] xs, List<int> ys) => [.. xs, .. ys];
					static HashSet<int> Set(int a) => [a, 2];
					static ImmutableArray<int> Immutable(int a) => [a, 2];
					static int SpanSum(int a, int b) {
						ReadOnlySpan<int> s = [a, b, 3];
						int sum = 0;
						foreach (var x in s)
							sum += x;
						return sum;
					}
					static int Params(params ReadOnlySpan<int> values) {
						int sum = 0;
						foreach (var x in values)
							sum += x;
						return sum;
					}
					static IEnumerable<int> SpreadList(List<int> xs, int y) => [.. xs, y];
					static IEnumerable<int> SpreadCollection(ICollection<int> xs, int y) => [y, .. xs];

					public static string Run() {
						var sb = new StringBuilder();
						void P(IEnumerable<int> e) => sb.Append(string.Join(",", e)).Append(';');
						P(ListOf(1, 2));
						P(ListSpread([1, 2], [3]));
						P(ArrayOf(4));
						P(ArraySpread([5], [6, 7]));
						P(Set(8));
						P(Immutable(9));
						sb.Append(SpanSum(1, 2)).Append(';');
						int v = sb.Length;
						sb.Append(Params(v, 2, 3) - v).Append(';');
						P(SpreadList([10, 11], 12));
						P(SpreadCollection(new List<int> { 13 }, 14));
						return sb.ToString();
					}
				}
			}
			""";

		/// <summary>
		/// Code that fills collections but isn't a collection expression, or is one that can't be written the same way:
		/// it must still behave the same (a capacity, a partly filled array, a list that's changed later, side effects)
		/// </summary>
		internal const string LookAlikes = """
			using System;
			using System.Collections.Generic;
			using System.Linq;
			using System.Text;

			namespace Collections3 {
				public static class Program {
					static int counter;
					static int Next() => ++counter;

					static int Capacity(IEnumerable<int> xs) {
						var list = new List<int>(100);
						list.AddRange(xs);
						list.Add(1);
						return list.Capacity * 1000 + list.Count;
					}

					static string PartlyFilled(int a) {
						var array = new int[3];
						int i = 0;
						array[i] = a;
						i++;
						return string.Join(",", array);
					}

					static string ChangedLater(IEnumerable<int> xs) {
						var list = new List<int>();
						list.AddRange(xs);
						list.Add(Next());
						list.Insert(0, Next());
						list.Add(Next());
						return string.Join(",", list);
					}

					static string Order() {
						var array = new int[3];
						int i = 0;
						array[i] = Next();
						i++;
						array[i] = Next() * 10;
						i++;
						array[i] = Next() * 100;
						return string.Join(",", array) + "/" + i;
					}

					static string SideEffects(int[] xs) {
						IEnumerable<int> e = [Next(), .. xs.Select(x => x + Next()), Next()];
						return string.Join(",", e);
					}

					static string Nested(int a) {
						List<List<int>> lists = [[a], [a, Next()], [.. new[] { 7, 8 }]];
						return string.Join("|", lists.Select(l => string.Join(",", l)));
					}

					static string Dictionary(int a) {
						var d = new Dictionary<int, int[]> { [a] = [a, a + 1] };
						return string.Join(",", d[a]);
					}

					public static string Run() {
						var sb = new StringBuilder();
						sb.Append(Capacity(new[] { 1, 2 })).Append(';');
						sb.Append(PartlyFilled(5)).Append(';');
						sb.Append(ChangedLater(new[] { 3 })).Append(';');
						sb.Append(Order()).Append(';');
						sb.Append(SideEffects(new[] { 10, 20 })).Append(';');
						sb.Append(Nested(9)).Append(';');
						sb.Append(Dictionary(4)).Append(';');
						return sb.ToString();
					}
				}
			}
			""";

		[Theory]
		[InlineData("Interfaces", false)]
		[InlineData("Interfaces", true)]
		[InlineData("OtherTargets", false)]
		[InlineData("OtherTargets", true)]
		[InlineData("LookAlikes", false)]
		[InlineData("LookAlikes", true)]
		public void ExportCompileRun_SameBehavior(string program, bool optimize) {
			var (source, ns) = program switch {
				"Interfaces" => (Interfaces, "Collections"),
				"OtherTargets" => (OtherTargets, "Collections2"),
				_ => (LookAlikes, "Collections3"),
			};
			var original = TestCompiler.Compile(nameof(CollectionExpressionTests) + program + optimize, ExportProjectTests.WithTargetFramework(source), optimize);
			var expected = ProgramRunner.Run(original.Image, ns);
			Assert.False(expected.StartsWith("EXCEPTION", StringComparison.Ordinal) || expected.StartsWith("CRASH", StringComparison.Ordinal), expected);
			var project = ExportProjectTests.Export(original.Load(), LanguageRunner.Create(), sdkStyle: true);
			var code = string.Join(Environment.NewLine, project.SourceFiles.Select(File.ReadAllText));
			log.WriteLine(code);
			// The compiler's types aren't exported or used
			Assert.DoesNotContain("<>z__", code);
			Assert.DoesNotContain("InlineArray", code);
			Assert.DoesNotContain("CollectionsMarshal", code);
			Assert.DoesNotContain(".CopyTo(", code);
			var (image, errors) = ExportProjectTests.Compile(project, nameof(CollectionExpressionTests) + program + optimize, TestCompiler.References);
			Assert.True(image is not null, string.Join(Environment.NewLine, errors));
			Assert.Equal(expected, ProgramRunner.Run(image!, ns));

			// The collection expressions
			string[] expectedCode = program switch {
				"Interfaces" => new[] {
					"return [a, b];", "return [a];", "return [..xs, y];", "return [..xs, 1, ..xs];", "return [..xs];", "return [0, ..xs];",
					"private static readonly IReadOnlyList<int> field = [1, 2, 3];", "IReadOnlyList<int> Property { get; } = [4, 5];",
					// Overloads, type inference, no target type: cast
					"Sum((IEnumerable<int>)[1, 2, 3])", "Sum((IReadOnlyList<int>)[1, 2], 3)", "Count([1, 2, 3, 4])", "Overloaded((IEnumerable<int>)[1, 2])",
					"Generic((IEnumerable<int>)[1, 2, 3])", "((IReadOnlyCollection<int>)[1, 2]).Count", "(IEnumerable<int>)[x, x]",
					"foreach (int item in (IEnumerable<int>)[a, a + 1])", "[[20], [21, 22]]",
					// The constructor's collections are field and property initializers again
					"class Holder(IEnumerable<string> names)", "= [..names, \"end\"];", "public IReadOnlyCollection<int> Values = [1];",
				},
				"OtherTargets" => new[] {
					"return [a, b, 3];", "return [..xs, 1, ..ys];", "return [..xs, ..ys];", "return [a, 2];", "ReadOnlySpan<int> readOnlySpan = [a, b, 3];",
					"Params([length, 2, 3])", "return [..xs, y];", "return [y, ..xs];", "ArraySpread(new int[1] { 5 }, [6, 7])", "SpreadList([10, 11], 12)",
				},
				_ => new[] {
					"List<int> list = new List<int>(100);", "new int[3];", "Insert(0, Next())", "List<int> list = [..xs, Next()];",
					"[Next(), ..xs.Select((int x) => x + Next()), Next()]", "[[a], [a, Next()], new int[2] { 7, 8 }.ToList()]",
				},
			};
			foreach (var line in expectedCode)
				Assert.Contains(line, code);
		}

		/// <summary>
		/// A buffer whose length is a variable that's computed from itself ('n = n + part.Length', 'count = count + count')
		/// looks like a builder. The length can't be proven, so it stays: following the variable to its value recursed
		/// forever and the stack overflow ended dnSpy (BouncyCastle's Asn1Utilities, System.Private.Xml's
		/// XmlQueryTypeFactory).
		/// </summary>
		internal const string SelfReferencingLength = """
			using System;
			using System.Text;

			namespace Collections4 {
				public static class Program {
					static byte[] Prepend(byte[][] parts, int index, int total) {
						byte[] part = parts[index];
						byte first = part[0];
						total = total + part.Length;
						byte[] result = new byte[total];
						result[0] = first;
						return result;
					}

					static uint[] Grow(uint[] digits, int count) {
						count = count + count;
						uint[] result = new uint[count];
						digits.CopyTo(result, 0);
						return result;
					}

					public static string Run() {
						var sb = new StringBuilder();
						var bytes = Prepend(new[] { new byte[] { 7, 8 }, new byte[] { 9 } }, 0, 3);
						sb.Append(bytes.Length).Append(':').Append(string.Join(",", bytes)).Append(';');
						var digits = Grow(new uint[] { 1, 2, 3 }, 3);
						sb.Append(digits.Length).Append(':').Append(string.Join(",", digits)).Append(';');
						return sb.ToString();
					}
				}
			}
			""";

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void SelfReferencingLength_StaysAndDoesntOverflow(bool optimize) {
			var name = nameof(SelfReferencingLength_StaysAndDoesntOverflow) + optimize;
			var original = TestCompiler.Compile(name, ExportProjectTests.WithTargetFramework(SelfReferencingLength), optimize);
			var expected = ProgramRunner.Run(original.Image, "Collections4");
			Assert.Equal("5:7,0,0,0,0;6:1,2,3,0,0,0;", expected);
			var project = ExportProjectTests.Export(original.Load(), LanguageRunner.Create(), sdkStyle: true);
			var code = string.Join(Environment.NewLine, project.SourceFiles.Select(File.ReadAllText));
			log.WriteLine(code);
			Assert.Contains("new byte[", code);
			Assert.Contains("new uint[", code);
			var (image, errors) = ExportProjectTests.Compile(project, name, TestCompiler.References);
			Assert.True(image is not null, string.Join(Environment.NewLine, errors));
			Assert.Equal(expected, ProgramRunner.Run(image!, "Collections4"));
		}

		/// <summary>
		/// 'params ReadOnlySpan&lt;object?&gt;' in nullable-enabled code (Microsoft.Extensions.Logging's
		/// string.Format(provider, CompositeFormat, a, b)): the compiler's inline array of 'object' converted to a span of
		/// 'object?' was shown as '&lt;&gt;y__InlineArray2&lt;object&gt; buffer = default; ...', which doesn't compile
		/// </summary>
		internal const string NullableParamsSpan = """
			#nullable enable
			using System;
			using System.Globalization;
			using System.Text;

			namespace Collections5 {
				public static class Program {
					static readonly CompositeFormat format = CompositeFormat.Parse("{0}-{1}");

					static string Format(object? a, string? b) {
						string? s = b;
						return string.Format(CultureInfo.InvariantCulture, format, s ?? a, b);
					}

					static string Join(params ReadOnlySpan<object?> values) => string.Join(",", values.ToArray());

					static string Call(object? a, string b) => Join(a, b, null);

					public static string Run() => Format(1, "x") + ";" + Format(2, null) + ";" + Call(3, "y") + ";" +
						new Formatter("{0}+{1}").Format(4, "z") + ";" + new Formatter("{0}+{1}").Format<int, string?>(5, null) + ";";
				}

				// Like Microsoft.Extensions.Logging's LogValuesFormatter
				sealed class Formatter(string text) {
					readonly CompositeFormat _format = CompositeFormat.Parse(text);

					static bool TryFormat<T>(T value, out object? formatted) {
						formatted = value is null ? "(null)" : null;
						return formatted is not null;
					}

					public string Format<TArg0, TArg1>(TArg0 arg0, TArg1 arg1) {
						if (!(TryFormat(arg0, out var stringValue) | TryFormat(arg1, out var stringValue2)))
							return string.Format(CultureInfo.InvariantCulture, _format, arg0, arg1);
						return string.Format(CultureInfo.InvariantCulture, _format, stringValue ?? arg0, stringValue2 ?? arg1);
					}
				}
			}
			""";

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void NullableParamsSpan_IsCollectionExpressionOrParams(bool optimize) {
			var name = nameof(NullableParamsSpan_IsCollectionExpressionOrParams) + optimize;
			var original = TestCompiler.Compile(name, ExportProjectTests.WithTargetFramework(NullableParamsSpan), optimize);
			var expected = ProgramRunner.Run(original.Image, "Collections5");
			Assert.Equal("x-x;2-;3,y,;4+z;5+(null);", expected);
			var project = ExportProjectTests.Export(original.Load(), LanguageRunner.Create(), sdkStyle: true);
			var code = string.Join(Environment.NewLine, project.SourceFiles.Select(File.ReadAllText));
			log.WriteLine(code);
			Assert.DoesNotContain("InlineArray", code);
			var (image, errors) = ExportProjectTests.Compile(project, name, TestCompiler.References);
			Assert.True(image is not null, string.Join(Environment.NewLine, errors));
			Assert.Equal(expected, ProgramRunner.Run(image!, "Collections5"));
		}

		[Fact]
		public void CompilerTypesAddedByAnEdit_AreCollectionExpressions() {
			// An edit compiles code that uses the compiler's types into a module that already has them: dnSpy's importer adds
			// the compiled copies as '<>z__ReadOnlyArray__0`1' etc. Their uses are collection expressions too (the code
			// compiles when it's edited again), and the tree view hides them.
			var module = TestCompiler.Compile(nameof(CompilerTypesAddedByAnEdit_AreCollectionExpressions), Interfaces).Load();
			var compilerTypes = module.Types.Where(t => t.Name.String.StartsWith("<>z__ReadOnly", StringComparison.Ordinal)).ToList();
			Assert.NotEmpty(compilerTypes);
			foreach (var type in compilerTypes) {
				var name = type.Name.String;
				type.Name = name.Substring(0, name.Length - 2) + "__0`1";
				Assert.True(Core.Transforms.CollectionExpressionTransform.IsCompilerType(type), type.Name);
			}
			var program = module.Types.Single(t => t.FullName == "Collections.Program");
			var code = LanguageRunner.Decompile(LanguageRunner.Create(), program).Text;
			log.WriteLine(code);
			Assert.DoesNotContain("<>z__", code);
			foreach (var line in new[] { "return [a, b];", "return [a];", "return [..xs, y];" })
				Assert.Contains(line, code);

			// Not the compiler's types
			foreach (var name in new[] { "<>z__ReadOnlyArray__`1", "<>z__ReadOnlyArray__x`1", "<>z__ReadOnlyArrays__0`1", "<>z__ReadOnlyArray__0" }) {
				compilerTypes[0].Name = name;
				Assert.False(Core.Transforms.CollectionExpressionTransform.IsCompilerType(compilerTypes[0]), name);
			}
		}
	}
}
