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

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Source of the programs the tests compile with Roslyn and then decompile
	/// </summary>
	static class TestPrograms {
		public const string ControlFlow = """
			using System;
			using System.Collections.Generic;
			using System.IO;

			public class ControlFlow {
				public int Loops(int[] values) {
					int sum = 0;
					for (int i = 0; i < values.Length; i++) {
						if (values[i] > 10)
							continue;
						sum += values[i];
					}
					foreach (var v in values)
						sum -= v;
					while (sum < 100)
						sum += 25;
					do {
						sum--;
					} while (sum > 1000);
					return sum;
				}

				public string Exceptions(string text) {
					try {
						using var reader = new StringReader(text);
						return reader.ReadToEnd();
					}
					catch (IOException ex) when (ex.HResult != 0) {
						return ex.Message;
					}
					finally {
						Console.WriteLine("done");
					}
				}

				public string Switch(object o) => o switch {
					int i when i > 0 => "positive",
					int => "int",
					string s => s,
					null => "null",
					_ => "other",
				};

				public int SwitchStatement(int value) {
					switch (value) {
					case 1:
						return 10;
					case 2:
					case 3:
						value *= 3;
						break;
					default:
						value = -value;
						break;
					}
					return value;
				}

				public int Lambdas(List<int> list) {
					int captured = 3;
					Func<int, int> f = x => x * captured;
					int Local(int y) => y + captured;
					list.Sort((a, b) => b.CompareTo(a));
					return f(Local(list.Count));
				}

				public int Enumerators(List<int> list, IEnumerable<string> names) {
					int total = 0;
					foreach (var item in list)
						total += item;
					foreach (string name in names) {
						if (name.Length == 0)
							break;
						total += name.Length;
					}
					return total;
				}

				public string Conditional(int[] values, string? text) {
					var first = values.Length > 0 ? values[0] : -1;
					text ??= "none";
					return text + first;
				}

				public void Locking(object gate, List<string> log) {
					lock (gate) {
						log.Add("in");
					}
					checked {
						int big = int.MaxValue - 1;
						log.Add((big + 1).ToString());
					}
				}
			}
			""";

		public const string StateMachines = """
			using System;
			using System.Collections.Generic;
			using System.Threading.Tasks;

			public class StateMachines {
				public async Task<int> Compute(int x) {
					int a = await Task.FromResult(x);
					await Task.Delay(1);
					try {
						a += await Other(a);
					}
					catch (InvalidOperationException) {
						a = -1;
					}
					return a;
				}

				async Task<int> Other(int v) {
					await Task.Yield();
					return v * 2;
				}

				public async void Fire() {
					await Task.Delay(1);
					Console.WriteLine("fired");
				}

				public IEnumerable<int> Numbers(int n) {
					for (int i = 0; i < n; i++) {
						if (i % 2 == 0)
							yield return i;
					}
				}

				// Same shape as Newtonsoft.Json's JsonWriter.InternalWriteEndAsync/AwaitRemaining, where upstream
				// produced sequence points that start inside an instruction
				int levels;
				bool property;
				Task WriteNullAsync(System.Threading.CancellationToken ct) => Task.CompletedTask;
				Task WriteEndAsync(int token, System.Threading.CancellationToken ct) => Task.CompletedTask;
				int Pop() => levels--;
				void UpdateCurrentState() => property = !property;

				public async Task AwaitRemaining(int levelsToComplete, System.Threading.CancellationToken cancellationToken) {
					while (levelsToComplete-- > 0) {
						int token = Pop();
						if (property) {
							await WriteNullAsync(cancellationToken).ConfigureAwait(false);
						}
						await WriteEndAsync(token, cancellationToken).ConfigureAwait(false);
						UpdateCurrentState();
					}
				}

				public async IAsyncEnumerable<int> Stream(int n) {
					for (int i = 0; i < n; i++) {
						await Task.Yield();
						yield return i;
					}
				}
			}
			""";

		/// <summary>
		/// Statement shapes whose IL is easy to attribute wrongly: switch expressions, filters, values kept on the
		/// stack, iterators and async methods with try blocks... <see cref="StatementsDriver"/> runs every path.
		/// </summary>
		public const string Statements = """
			using System;
			using System.Collections.Generic;
			using System.IO;
			using System.Linq;
			using System.Threading.Tasks;

			public record Rec(int A, string B);

			public class GenericException<T> : Exception {
				public T Value { get; }
				public GenericException(T value) => Value = value;
			}

			public class Statements {
				public event EventHandler? Error;
				string? cached;

				public string SwitchDecl(int k) {
					var r = k switch {
						1 => "a",
						2 => "b",
						_ => "c",
					};
					return r;
				}

				public string SwitchReturn(object o) {
					return o switch {
						int i when i > 3 => "big",
						int => "int",
						string s => s,
						_ => "other",
					};
				}

				public string SwitchThrow(int k) {
					try {
						return k switch {
							1 => throw new InvalidOperationException(),
							2 => throw new ArgumentException(),
							_ => "ok",
						};
					}
					catch (InvalidOperationException) {
						return "ioe";
					}
					catch (ArgumentException) {
						return "ae";
					}
				}

				public int Ternary(bool a) {
					return a ? 1 : 2;
				}

				public string Coalesce(string? s) {
					return s ?? throw new ArgumentNullException(nameof(s));
				}

				public void Events(EventArgs e) {
					Error?.Invoke(this, e);
				}

				public string Assign(string? v) {
					cached ??= v ?? "d";
					var x = v is { Length: > 2 } ? v.Substring(1) : v;
					return x + cached;
				}

				public int Loops(int[] values, List<string> names) {
					int sum = 0;
					for (int i = 0, j = values.Length - 1; i < j; i++, j--) {
						if (values[i] == 0)
							continue;
						if (values[j] < 0)
							break;
						sum += values[i] * values[j];
					}
					foreach (var n in names) {
						if (n is null or "")
							continue;
						if (n.Length > 10)
							break;
						sum += n.Length;
					}
					while (sum is > 100 and < 1000)
						sum /= 2;
					do {
						sum++;
						if (sum == 50)
							goto done;
					} while (sum < 20);
				done:
					return sum;
				}

				public int Switch(object o) {
					switch (o) {
					case int i when i > 0:
						return i;
					case string s:
						if (s.Length == 0)
							goto default;
						return s.Length;
					case null:
						return -1;
					default:
						return 0;
					}
				}

				public string Exceptions(string path) {
					try {
						using var r = new StringReader(path);
						try {
							if (path == "io")
								throw new IOException("io");
							if (path == "x")
								throw new InvalidOperationException("x");
							return r.ReadToEnd();
						}
						catch (IOException e) when (e.Message.Length > 0 && e.HResult != 1) {
							return e.Message;
						}
						catch (Exception e) when (e is InvalidOperationException { Message: "x" }) {
							throw;
						}
						finally {
							Console.WriteLine("inner");
						}
					}
					catch (Exception ex) {
						return ex.GetType().Name;
					}
				}

				public unsafe int Unsafe(int[] a) {
					Span<int> s = stackalloc int[4];
					fixed (int* p = a) {
						s[0] = *p;
					}
					lock (a) {
						checked {
							s[1] = a.Length + 1;
						}
					}
					var (x, y) = (s[0], s[1]);
					return x + y;
				}

				public string Strings(int a, Rec r) {
					var t = $"{a} {r.B} {a + 1:X}";
					var r2 = r with { A = 3 };
					int[] arr = [1, 2, a];
					return t + r2 + arr.Length;
				}

				public IEnumerable<int> Iter(int n) {
					try {
						for (int i = 0; i < n; i++) {
							if (i == 5)
								yield break;
							yield return i;
						}
					}
					finally {
						Console.WriteLine("done");
					}
				}

				public async Task<int> Async(IAsyncEnumerable<int> e, Stream s) {
					int t = 0;
					await foreach (var v in e) {
						t += v;
					}
					await using (var d = s) {
						try {
							t += await Task.FromResult(1);
							if (t == 4)
								throw new InvalidOperationException("t");
						}
						catch (Exception ex) when (ex.Message != "") {
							await Task.Delay(1);
						}
						finally {
							await Task.Yield();
						}
					}
					var q = await Task.FromResult(2) switch {
						1 => 5,
						_ => 6,
					};
					return t + q;
				}

				public int GenericCatch(int v) {
					try {
						throw new GenericException<int>(v);
					}
					catch (GenericException<int> e) when (e.Value > 0) {
						return e.Value;
					}
					catch (GenericException<int> e) {
						return -e.Value;
					}
				}

				public int Lambdas(List<int> l) {
					int c = 1;
					Func<int, int> f = x => {
						if (x > c)
							return x;
						return c;
					};
					l.ForEach(v => { c += v; });
					return l.Where(v => v > c).Select(f).Sum();
				}
			}
			""";

		/// <summary>
		/// Runs every path of <see cref="Statements"/> (each switch arm, filters that match and don't...)
		/// </summary>
		public const string StatementsDriver = """

			public static class Driver {
				public static void Run() {
					var s = new Statements();
					s.SwitchDecl(1); s.SwitchDecl(2); s.SwitchDecl(3);
					s.SwitchReturn(5); s.SwitchReturn(1); s.SwitchReturn("t"); s.SwitchReturn(2.0);
					s.SwitchThrow(1); s.SwitchThrow(2); s.SwitchThrow(3);
					s.Ternary(true); s.Ternary(false);
					s.Coalesce("a");
					try { s.Coalesce(null); } catch (ArgumentNullException) { }
					s.Events(EventArgs.Empty);
					s.Error += (o, e) => { };
					s.Events(EventArgs.Empty);
					s.Assign(null); s.Assign("abcd"); s.Assign("ab");
					s.Loops(new[] { 1, 0, 3, -2, 5, 6 }, new List<string> { "a", "", "abcdefghijkl", "b" });
					s.Loops(new[] { 7, 9 }, new List<string> { "abc" });
					s.Loops(new int[0], new List<string>());
					s.Switch(3); s.Switch(-3); s.Switch("abc"); s.Switch(""); s.Switch(null!); s.Switch(1.0);
					s.Exceptions("text"); s.Exceptions("io"); s.Exceptions("x"); s.Exceptions(null!);
					s.Unsafe(new[] { 1, 2 });
					s.Strings(1, new Rec(1, "b"));
					foreach (var i in s.Iter(3)) { }
					foreach (var i in s.Iter(7)) { }
					s.Async(Numbers(3), new MemoryStream()).GetAwaiter().GetResult();
					s.Async(Numbers(2), new MemoryStream()).GetAwaiter().GetResult();
					s.Lambdas(new List<int> { 1, 2, 5 });
					s.GenericCatch(1);
					s.GenericCatch(-1);
				}

				static async IAsyncEnumerable<int> Numbers(int n) {
					for (int i = 1; i <= n; i++) {
						await Task.Yield();
						yield return i;
					}
				}
			}
			""";

		/// <summary>
		/// Every kind of definition the token maps must handle
		/// </summary>
		public const string Members = """
			using System;
			using System.Collections.Generic;

			public class Container<T> where T : class {
				public event EventHandler? Changed;
				public T? Value { get; set; }
				public int this[int index] => index * 2;
				public U Convert<U>(T value, Func<T, U> converter) where U : struct => converter(value);
				public void Raise() => Changed?.Invoke(this, EventArgs.Empty);
				public class Nested<V> {
					public V? Item;
				}
			}

			public static class Constants {
				public static int Answer() => 42;
			}
			""";

		/// <summary>
		/// C# 11-14 features (dnSpy's old decompiler, a fork of ILSpy 2.x, didn't understand them)
		/// </summary>
		public const string ModernCSharp = """"
			using System;
			using System.Collections.Generic;
			using System.Runtime.CompilerServices;

			namespace Modern {
				public class Point(int x, int y) {
					public int X => x;
					public int Y => y;
				}

				public static class StringExtensions {
					extension(string s) {
						public bool IsShort => s.Length < 5;
						public string Twice() => s + s;
					}
				}

				public class FieldKeyword {
					public string Name {
						get;
						set => field = value.Trim();
					} = "";
				}

				public class Required {
					public required string Name { get; init; }
				}

				public record Person(string First, string Last);

				[InlineArray(4)]
				public struct Buffer4 {
					private int element;
				}

				public ref struct RefHolder {
					public ref int Value;
				}

				public class Patterns {
					public bool IsSmall(int[] values) => values is [_, _] or [];
					public string Describe(object o) => o is Person { First: var first } ? first : "?";
					public ReadOnlySpan<byte> Utf8() => "hello"u8;
					public string Raw() => $$"""{"x": {{1 + 1}}}""";
				}
			}
			"""";
	}
}
