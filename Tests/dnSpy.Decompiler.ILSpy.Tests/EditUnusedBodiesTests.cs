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
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Edit Method on a big type decompiles it from an image without the bodies of the methods it doesn't show
	/// (UpstreamCSharpLanguage.GetUnusedBodies). Code the compiler generated for those methods then has nothing that
	/// uses it, and upstream showed it with the compiler's names ('&lt;&gt;c__DisplayClass294_0',
	/// 'struct &lt;DisposeAsync&gt;d__10'), which don't compile: no method of Discord.Net's SocketGuild or of
	/// System.Linq.Async's AsyncEnumerable could be edited.
	/// </summary>
	public class EditUnusedBodiesTests {
		readonly ITestOutputHelper log;
		public EditUnusedBodiesTests(ITestOutputHelper log) => this.log = log;

		const string Program = """
			using System;
			using System.Linq;
			using System.Text;
			using System.Threading.Tasks;

			namespace EditUnused {
				public static class Program {
					static int Simple(int a) => a + 1;

					// A lambda in an async method: its closure is used by the state machine's code
					static async Task<int> AsyncWithLambda(int[] xs, int k) {
						await Task.Yield();
						Func<int, int> times = x => x * k;
						return xs.Select(times).Sum();
					}

					static int Closure(int n) {
						Func<int, int> twice = v => v * 2 + n;
						return twice(n);
					}

					static int StaticLambda(int[] xs) => xs.Count(x => x > 1);

					static int LocalFunction(int n) {
						int Add(int x) => x + n;
						return Add(1);
					}

					// Async methods of a generic nested type: their state machines are nested in it
					sealed class Iterator<T, TKey> {
						public async ValueTask<int> DisposeAsync(int a) {
							await Task.Yield();
							return a * 2;
						}

						public async Task<string> WithLambda(T value, TKey key) {
							await Task.Yield();
							Func<string> text = () => value + "/" + key;
							return text();
						}
					}

					public static string Run() {
						var sb = new StringBuilder();
						sb.Append(Simple(1)).Append(';');
						sb.Append(AsyncWithLambda(new[] { 1, 2, 3 }, 10).Result).Append(';');
						sb.Append(Closure(4)).Append(';');
						sb.Append(StaticLambda(new[] { 1, 2, 3 })).Append(';');
						sb.Append(LocalFunction(5)).Append(';');
						var iterator = new Iterator<string, int>();
						sb.Append(iterator.DisposeAsync(21).Result).Append(';');
						sb.Append(iterator.WithLambda("a", 7).Result).Append(';');
						return sb.ToString();
					}
				}
			}
			""";

		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void EditMethod_TypeWithoutUnusedBodies_Compiles(bool optimize) {
			var compiled = TestCompiler.Compile(nameof(EditUnusedBodiesTests) + optimize, Program, optimize);
			var expected = ProgramRunner.Run(compiled.Image, "EditUnused");
			Assert.Equal("2;60;12;2;6;42;a/7;", expected);
			var decompiler = LanguageRunner.Create();
			// Always (the tests' type is small)
			decompiler.EditUnusedBodiesMinSize = 0;
			var methods = EditRoundTripTests.GetEditableMethods(decompiler, compiled.Load()).ToList();
			Assert.True(methods.Count >= 9, string.Join(", ", methods.Select(a => a.Name.String)));
			var failures = new List<string>();
			foreach (var method in methods) {
				var outcome = EditRoundTripTests.RunEdit(decompiler, EditKind.EditMethod, compiled, "EditUnused", expected, method.MDToken.Raw);
				if (outcome is not null)
					failures.Add(outcome.Description);
			}
			foreach (var f in failures)
				log.WriteLine(f);
			Assert.Empty(failures);
			// The optimization is still used (for the methods without lambdas, local functions and closures)
			Assert.True(decompiler.ImagesWithoutUnusedBodies > 0, "The decompiled image always had all the code");
		}
	}
}
