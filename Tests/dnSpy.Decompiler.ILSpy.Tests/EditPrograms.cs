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
	/// Programs for the edit code tests: each one's Program.Run() exercises all its code and returns a string that
	/// depends on what every method computed, so an edit that changes behavior changes it.
	/// </summary>
	static class EditPrograms {
		/// <summary>
		/// Classic C# (up to C# 11): what the edit code commands were used on until now
		/// </summary>
		public const string Classic = """
			#nullable disable
			using System;
			using System.Collections;
			using System.Collections.Generic;
			using System.IO;
			using System.Linq;
			using System.Text;
			using System.Threading;
			using System.Threading.Tasks;

			namespace EditClassic {
				public interface IShape {
					double Area { get; }
					string Name { get; }
				}

				public abstract class Shape : IShape {
					static int created;
					protected readonly string name;
					public static int Created => created;

					protected Shape(string name) {
						this.name = name;
						created++;
					}

					public abstract double Area { get; }
					public virtual string Name => name;
					public override string ToString() => $"{Name}:{Area:F2}";
				}

				public sealed class Circle : Shape, IComparable<Circle> {
					public double Radius { get; }
					public Circle(double radius) : base("circle") => Radius = radius;
					public override double Area => Math.PI * Radius * Radius;
					public int CompareTo(Circle? other) => Radius.CompareTo(other?.Radius ?? 0);
				}

				public class Rect : Shape {
					double width, height = 1;
					public Rect() : this(1, 1) { }
					public Rect(double width, double height) : base("rect") {
						this.width = width;
						this.height = height;
					}
					public override double Area => width * height;
					public override string Name => base.Name + (width == height ? "(square)" : "");
					public double this[int side] => side % 2 == 0 ? width : height;
				}

				public struct Point : IEquatable<Point> {
					public int X;
					public int Y { get; set; }
					public Point(int x, int y) {
						X = x;
						Y = y;
					}
					public static Point operator +(Point a, Point b) => new Point(a.X + b.X, a.Y + b.Y);
					public static bool operator ==(Point a, Point b) => a.Equals(b);
					public static bool operator !=(Point a, Point b) => !a.Equals(b);
					public static implicit operator (int, int)(Point p) => (p.X, p.Y);
					public static explicit operator Point(int v) => new Point(v, v);
					public bool Equals(Point other) => X == other.X && Y == other.Y;
					public override bool Equals(object? obj) => obj is Point p && Equals(p);
					public override int GetHashCode() => X * 31 + Y;
					public override string ToString() => $"({X},{Y})";
				}

				public enum Color : byte { Red = 1, Green = 2, Blue = 4, All = Red | Green | Blue }

				[Flags]
				public enum Access { None = 0, Read = 1, Write = 2 }

				public delegate int Transform(int value);

				public class Counter {
					int count = 10;
					readonly List<string> log = new List<string> { "start" };
					public static readonly string Prefix = "c";
					static readonly Dictionary<string, int> registry;
					public event EventHandler? Changed;
					EventHandler? custom;
					public event EventHandler Custom {
						add { custom += value; log.Add("add"); }
						remove { custom -= value; log.Add("remove"); }
					}

					static Counter() {
						registry = new Dictionary<string, int>();
						registry["init"] = 1;
					}

					public Counter() { }
					public Counter(int start) => count = start;

					public int Count {
						get => count;
						set {
							if (value == count)
								return;
							count = value;
							Changed?.Invoke(this, EventArgs.Empty);
							custom?.Invoke(this, EventArgs.Empty);
						}
					}
					public string Label { get; init; } = "none";
					public int ReadOnlyAuto { get; } = 7;
					public string Log => string.Join(",", log);
					public static int Registered => registry.Count;

					public void Increment(int by = 1) => Count += by;
					public bool TryGet(string key, out int value) => registry.TryGetValue(key, out value);
					public ref int CountRef() => ref count;
					public void Swap(ref int a, ref int b) => (a, b) = (b, a);
					public int Sum(params int[] values) {
						int s = 0;
						foreach (var v in values)
							s += v;
						return s;
					}
					public int ReadIn(in Point p) => p.X + p.Y;
				}

				public class Box<T> where T : IComparable<T> {
					readonly List<T> items = new List<T>();
					public void Add(T item) => items.Add(item);
					public T Max() {
						T max = items[0];
						foreach (var item in items) {
							if (item.CompareTo(max) > 0)
								max = item;
						}
						return max;
					}
					public TResult Fold<TResult>(TResult seed, Func<TResult, T, TResult> f) {
						var acc = seed;
						foreach (var item in items)
							acc = f(acc, item);
						return acc;
					}
					public class Node {
						public T? Value;
						public Node? Next;
						public int Length() => 1 + (Next?.Length() ?? 0);
					}
				}

				public static class Extensions {
					public static string Repeat(this string s, int n) {
						var sb = new StringBuilder();
						for (int i = 0; i < n; i++)
							sb.Append(s);
						return sb.ToString();
					}
					public static IEnumerable<int> Evens(this IEnumerable<int> source) => source.Where(x => x % 2 == 0);
				}

				public class Algorithms {
					public static IEnumerable<int> Fibonacci(int count) {
						int a = 0, b = 1;
						for (int i = 0; i < count; i++) {
							yield return a;
							(a, b) = (b, a + b);
						}
					}

					public static async Task<int> SumAsync(int[] values) {
						int total = 0;
						foreach (var v in values) {
							await Task.Yield();
							total += v;
						}
						return total;
					}

					public static string Classify(object o) {
						switch (o) {
						case int i when i < 0:
							return "negative";
						case int i:
							return "int " + i;
						case string s:
							return "string " + s.Length;
						case null:
							return "null";
						default:
							return o.GetType().Name;
						}
					}

					public static string Grade(int score) => score switch {
						>= 90 => "A",
						>= 80 => "B",
						_ => "C",
					};

					public static int Closures(int n) {
						int captured = n;
						Func<int, int> add = x => x + captured;
						captured++;
						int Local(int y) => y * captured;
						static int Twice(int z) => z * 2;
						return add(Local(Twice(1)));
					}

					public static string Exceptions(string input) {
						var sb = new StringBuilder();
						try {
							try {
								if (input.Length == 0)
									throw new InvalidOperationException("empty");
								sb.Append(int.Parse(input));
							}
							catch (FormatException ex) when (ex.Message.Length > 0) {
								sb.Append("format");
							}
							finally {
								sb.Append(";");
							}
						}
						catch (InvalidOperationException ex) {
							sb.Append(ex.Message);
						}
						return sb.ToString();
					}

					public static string Linq(int[] values) {
						var q = from v in values
								where v > 1
								orderby v descending
								select v * 10;
						var groups = values.GroupBy(v => v % 2).Select(g => g.Key + ":" + g.Count());
						return string.Join(",", q) + "|" + string.Join(",", groups);
					}

					public static unsafe int Unsafe(int[] values) {
						int sum = 0;
						fixed (int* p = values) {
							for (int i = 0; i < values.Length; i++)
								sum += p[i];
						}
						int* stack = stackalloc int[2];
						stack[0] = sum;
						return stack[0];
					}

					public static string Resources() {
						var sb = new StringBuilder();
						var gate = new object();
						lock (gate) {
							using (var reader = new StringReader("a\nb")) {
								string? line;
								while ((line = reader.ReadLine()) is not null)
									sb.Append(line);
							}
						}
						using var writer = new StringWriter();
						writer.Write("w");
						return sb.ToString() + writer;
					}

					public static int Checked(int a) {
						try {
							return checked(a * 1000000);
						}
						catch (OverflowException) {
							return -1;
						}
					}

					public static string Strings(int n, double d) {
						var s = $"n={n,3} d={d:F1} {{literal}}";
						return s + @"\path" + "\t".Length + nameof(Strings) + typeof(List<int>).Name + default(int) + (n is > 0 and < 10 ? "small" : "big");
					}

					public static string Tuples() {
						var (a, b) = (1, "two");
						(int x, int y) point = (3, 4);
						var nested = (a, (b, point.x));
						return $"{a}{b}{point.x + point.y}{nested.Item2.b}";
					}

					public static string Nullables(int? value) {
						int? doubled = value * 2;
						string? text = value?.ToString();
						return (doubled ?? -1) + (text ?? "null") + (value.HasValue ? value.GetValueOrDefault() : 0);
					}

					public static int Gotos(int n) {
						int i = 0;
					loop:
						if (i < n) {
							i += 2;
							goto loop;
						}
						return i;
					}

					public static string Arrays() {
						int[,] grid = new int[2, 3];
						grid[1, 2] = 5;
						int[][] jagged = { new[] { 1 }, new[] { 2, 3 } };
						var list = new List<int>(jagged[1]) { 4 };
						var dict = new Dictionary<string, int> { ["a"] = 1, ["b"] = 2 };
						return grid[1, 2] + "," + jagged.Length + "," + list.Count + "," + dict["b"];
					}
				}

				public record Person(string First, string Last) {
					public string Full => First + " " + Last;
				}

				public record struct Pair(int A, int B);

				public class Holder {
					public int Value { get; set; }
					public Holder Clone() => new Holder { Value = Value };
				}

				public static class Program {
					static string Area(IShape s) => s.Name + "=" + s.Area.ToString("F1");

					public static string Run() {
						var r = new List<string>();
						var shapes = new List<Shape> { new Circle(1), new Rect(2, 3), new Rect() };
						r.Add(string.Join(";", shapes.Select(Area)));
						r.Add(shapes[0].ToString() + new Rect(2, 2)[1] + Shape.Created);
						r.Add(new Circle(2).CompareTo(new Circle(1)).ToString());
						var p = new Point(1, 2) + (Point)3;
						(int, int) t = p;
						r.Add(p + " " + t + (p == new Point(4, 5)) + (p != p) + p.GetHashCode());
						r.Add(Color.All + " " + (Access.Read | Access.Write) + (int)Color.Blue);
						Transform tr = v => v * 3;
						r.Add(tr(5).ToString());
						var c = new Counter(5) { Label = "lbl" };
						int changed = 0;
						c.Changed += (s, e) => changed++;
						EventHandler h = (s, e) => changed += 10;
						c.Custom += h;
						c.Increment();
						c.Increment(3);
						c.Custom -= h;
						c.Count = c.Count;
						c.TryGet("init", out int init);
						int x = 1, y = 2;
						c.Swap(ref x, ref y);
						c.CountRef() = 42;
						r.Add($"{c.Count} {changed} {c.Label} {c.ReadOnlyAuto} {c.Log} {init} {x}{y} {c.Sum(1, 2, 3)} {c.ReadIn(new Point(2, 3))} {Counter.Prefix} {Counter.Registered} {new Counter().Count}");
						var box = new Box<int>();
						box.Add(3);
						box.Add(9);
						box.Add(4);
						var node = new Box<int>.Node { Value = 1, Next = new Box<int>.Node() };
						r.Add(box.Max() + " " + box.Fold("", (acc, v) => acc + v) + " " + node.Length());
						r.Add("ab".Repeat(3) + string.Join(",", new[] { 1, 2, 3, 4 }.Evens()));
						r.Add(string.Join(",", Algorithms.Fibonacci(8)));
						r.Add(Algorithms.SumAsync(new[] { 1, 2, 3 }).Result.ToString());
						r.Add(Algorithms.Classify(-1) + Algorithms.Classify(3) + Algorithms.Classify("xy") + Algorithms.Classify(null!) + Algorithms.Classify(1.5));
						r.Add(Algorithms.Grade(95) + Algorithms.Grade(85) + Algorithms.Grade(10));
						r.Add(Algorithms.Closures(3).ToString());
						r.Add(Algorithms.Exceptions("12") + Algorithms.Exceptions("x") + Algorithms.Exceptions(""));
						r.Add(Algorithms.Linq(new[] { 1, 2, 3, 4 }));
						r.Add(Algorithms.Unsafe(new[] { 1, 2, 3 }).ToString());
						r.Add(Algorithms.Resources());
						r.Add(Algorithms.Checked(5) + " " + Algorithms.Checked(5000));
						r.Add(Algorithms.Strings(5, 2.25));
						r.Add(Algorithms.Tuples());
						r.Add(Algorithms.Nullables(4) + Algorithms.Nullables(null));
						r.Add(Algorithms.Gotos(5).ToString());
						r.Add(Algorithms.Arrays());
						var person = new Person("Ada", "Lovelace");
						var other = person with { First = "Al" };
						r.Add(person.Full + " " + other + (person == other) + new Pair(1, 2));
						var holder = new Holder { Value = 3 }.Clone();
						r.Add(holder.Value.ToString());
						return string.Join(Environment.NewLine, r);
					}
				}
			}
			""";

		/// <summary>
		/// C# 12-14 features (dnSpy's old decompiler predates them)
		/// </summary>
		/// <summary>
		/// A library as compiled for an older framework: its own copies of newer types, an attribute a source generator
		/// added (other compilations don't see it) and a call of an API marked [Experimental]
		/// </summary>
		public const string Compat = """
			using System;
			using System.Diagnostics.CodeAnalysis;

			namespace System.Diagnostics.CodeAnalysis {
				[AttributeUsage(AttributeTargets.Parameter)]
				internal sealed class NotNullWhenAttribute : Attribute {
					public NotNullWhenAttribute(bool returnValue) => ReturnValue = returnValue;
					public bool ReturnValue { get; }
				}
			}

			namespace System.Runtime.CompilerServices {
				internal static class IsExternalInit { }
			}

			namespace System {
				internal readonly struct Index {
					readonly int value;
					public Index(int value, bool fromEnd = false) => this.value = fromEnd ? ~value : value;
					public int Value => value < 0 ? ~value : value;
					public bool IsFromEnd => value < 0;
					public int GetOffset(int length) => IsFromEnd ? length - ~value : value;
				}
			}

			namespace Microsoft.CodeAnalysis {
				internal sealed class EmbeddedAttribute : Attribute { }
			}

			namespace EditCompat {
				[Microsoft.CodeAnalysis.Embedded]
				[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
				internal sealed class GeneratedMarkerAttribute : Attribute {
					public GeneratedMarkerAttribute(string name) => Name = name;
					public string Name { get; }
				}

				public static class Preview {
					[Experimental("COMPAT001")]
					public static int Answer() => 42;
				}

				[GeneratedMarker("settings")]
				public class Settings {
					public string Name { get; init; } = "";
				}

				public static class Text {
					public static bool TryGet(string? s, [NotNullWhen(true)] out string? value) {
						value = s;
						return s is not null;
					}

					public static char FromEnd(string s, int n) {
						Index i = ^n;
						return s[i];
					}

					[GeneratedMarker("calls")]
					public static int CallsPreview() {
			#pragma warning disable COMPAT001
						return Preview.Answer();
			#pragma warning restore COMPAT001
					}
				}

				// Nullable metadata: oblivious code (#nullable disable) next to annotated code
			#nullable disable
				public class LegacyNames {
					public string Name = "legacy";
					public string Echo(string s) => s + Name;
					public System.Collections.Generic.List<string> Items { get; } = new System.Collections.Generic.List<string>();
				}
			#nullable restore

				public class Mixed {
					public string? Maybe(string s) => s.Length > 3 ? s : null;
			#nullable disable
					public string Oblivious(string s) => s;
			#nullable restore
					public System.Collections.Generic.Dictionary<string, string?> Map { get; } = new();
				}

				// Oblivious base types, interfaces and generic parameters in an annotated type
			#nullable disable
				public class MixedBase<T> : System.Collections.Generic.List<string>, System.IComparable<string>
			#nullable restore
				{
					public string? First => Count == 0 ? null : this[0];
					public int CompareTo(string? other) => Count;
			#nullable disable
					public T Pick<U>(T t, U u) where U : System.Collections.Generic.IList<string> => t;
			#nullable restore
					public U? Find<U>(U? u) where U : class => u;
				}

				public static class Program {
					public static string Run() {
						var settings = new Settings { Name = "n" };
						return (Text.TryGet("x", out var v) ? v : "") + Text.FromEnd("abc", 1) + settings.Name + Text.CallsPreview() +
							typeof(Settings).GetCustomAttributes(typeof(GeneratedMarkerAttribute), false).Length +
							typeof(Text).GetMethod("CallsPreview")!.GetCustomAttributes(typeof(GeneratedMarkerAttribute), false).Length +
							new LegacyNames().Echo("x") + new Mixed().Maybe("abcd") + new Mixed().Oblivious("o") + new Mixed().Map.Count +
							new MixedBase<int> { "m" }.First + new MixedBase<int>().Pick(1, new string[0]) + new MixedBase<int>().Find("f");
					}
				}
			}
			""";

		public const string Modern = """"
			using System;
			using System.Collections.Generic;
			using System.Diagnostics.CodeAnalysis;
			using System.Linq;
			using System.Numerics;
			using System.Runtime.CompilerServices;
			using System.Text;
			using System.Threading;
			using System.Threading.Tasks;
			using Pair = (int Left, int Right);

			namespace EditModern {
				// C# 12: primary constructors
				public class Account(string owner, decimal balance) {
					readonly List<string> history = new();
					public string Owner => owner;
					public decimal Balance { get; private set; } = balance;
					public void Deposit(decimal amount) {
						Balance += amount;
						history.Add($"{owner}+{amount}");
					}
					public string History => string.Join(",", history);
				}

				public class Savings(string owner, decimal balance, decimal rate) : Account(owner, balance) {
					public decimal Interest() => Balance * rate;
				}

				public readonly struct Vector2(double x, double y) {
					public double X { get; } = x;
					public double Y { get; } = y;
					public double Length => Math.Sqrt(X * X + Y * Y);
				}

				// C# 12: inline arrays
				[InlineArray(4)]
				public struct Buffer4 {
					int element;
				}

				// C# 11: ref fields, scoped
				public ref struct Cursor {
					ref int target;
					public Cursor(ref int value) => target = ref value;
					public void Bump(scoped ReadOnlySpan<int> deltas) {
						foreach (var d in deltas)
							target += d;
					}
				}

				// C# 11: required members
				public class Settings {
					public required string Name { get; init; }
					public int Retries { get; set; } = 3;
					[SetsRequiredMembers]
					public Settings(string name) => Name = name;
					public Settings() { }
				}

				// C# 11: static abstract interface members, generic math
				public interface IHasZero<TSelf> where TSelf : IHasZero<TSelf> {
					static abstract TSelf Zero { get; }
					static virtual string Describe() => "zero";
				}

				public static class Zero {
					public static string Describe<T>() where T : IHasZero<T> => T.Describe();
				}

				public readonly record struct Money(decimal Amount) : IHasZero<Money>, IAdditionOperators<Money, Money, Money> {
					public static Money Zero => new(0);
					public static Money operator +(Money a, Money b) => new(a.Amount + b.Amount);
				}

				public static class GenericMath {
					public static T Sum<T>(IEnumerable<T> values) where T : IHasZero<T>, IAdditionOperators<T, T, T> {
						T total = T.Zero;
						foreach (var v in values)
							total += v;
						return total;
					}
					public static T Max<T>(T a, T b) where T : INumber<T> => T.Max(a, b);
				}

				// C# 11: file-local types
				file static class Secret {
					public static int Code => 42;
				}

				public static class FileUser {
					public static int Use() => Secret.Code;
				}

				// C# 14: the field keyword
				public class Temperature {
					public double Celsius {
						get;
						set => field = Math.Round(value, 1);
					}
					public string Unit {
						get => field ??= "C";
						set;
					}
				}

				// C# 14: extension members
				public static class StringExtensions {
					extension(string s) {
						public bool IsShort => s.Length < 4;
						public string Shout() => s.ToUpperInvariant() + "!";
					}
					extension(int) {
						public static int Triple(int v) => v * 3;
					}
				}

				// C# 13: params collections, the Lock type
				public class Logger {
					readonly Lock gate = new();
					readonly List<string> lines = [];
					public void Write(params ReadOnlySpan<string> parts) {
						lock (gate) {
							foreach (var p in parts)
								lines.Add(p);
						}
					}
					public string Text => string.Join("|", lines);
				}

				// C# 13: partial properties
				public partial class Profile {
					public partial string Nick { get; set; }
				}
				public partial class Profile {
					string nick = "";
					public partial string Nick {
						get => nick;
						set => nick = value.Trim();
					}
				}

				// C# 11: generic attributes
				[AttributeUsage(AttributeTargets.Method)]
				public sealed class TagAttribute<T> : Attribute {
					public string Name => typeof(T).Name;
				}

				// C# 13: ref structs implementing interfaces, allows ref struct
				public interface ISized {
					int Size { get; }
				}

				public ref struct SizedSpan : ISized {
					readonly Span<int> span;
					public SizedSpan(Span<int> span) => this.span = span;
					public int Size => span.Length;
				}

				public static class RefStructGenerics {
					public static int Size<T>(T value) where T : ISized, allows ref struct => value.Size;
				}

				// C# 11: checked user-defined operators
				public readonly struct Small(byte value) {
					public byte Value => value;
					public static Small operator +(Small a, Small b) => new((byte)(a.Value + b.Value));
					public static Small operator checked +(Small a, Small b) => new(checked((byte)(a.Value + b.Value)));
				}

				// C# 14: user-defined compound assignment and instance increment operators
				public class Counter {
					// (Add Class Members: a nested enum is all in the main document)
					public enum Step : byte { One = 1, Five = 5 }
					public int Value { get; private set; }
					public void operator +=(int n) => Value += n;
					public void operator ++() => Value++;
					public void Add(Step step) => Value += (int)step;
				}

				// C# 14: partial constructors and events
				public partial class Widget {
					public partial Widget(string name);
					public partial event Action? Changed;
				}
				public partial class Widget {
					readonly string name;
					Action? changed;
					public partial Widget(string name) => this.name = name;
					public partial event Action? Changed {
						add => changed += value;
						remove => changed -= value;
					}
					public string Rename(string newName) {
						changed?.Invoke();
						return name + ">" + newName;
					}
				}

				public static class Features {
					// C# 12: collection expressions
					public static int[] Collections(int n) {
						int[] a = [1, 2, n];
						List<int> list = [.. a, 4];
						Span<int> span = [5, 6];
						IEnumerable<int> empty = [];
						return [.. list, span[1], empty.Count()];
					}

					// C# 11: list patterns, raw strings, UTF-8 literals
					public static string Patterns(int[] values) {
						var shape = values switch {
							[] => "empty",
							[var only] => "one:" + only,
							[var first, .., var last] => $"{first}..{last}",
						};
						var raw = """
							raw "text"
							""";
						ReadOnlySpan<byte> utf8 = "hé"u8;
						return shape + raw.Length + utf8.Length;
					}

					[Tag<int>]
					public static string Attributes() => typeof(Features).GetMethod(nameof(Attributes))!.GetCustomAttributes(false).Length.ToString();

					public static int InlineArrays() {
						var b = new Buffer4();
						for (int i = 0; i < 4; i++)
							b[i] = i * i;
						int sum = 0;
						foreach (var v in b)
							sum += v;
						return sum;
					}

					public static int RefFields() {
						int value = 1;
						var cursor = new Cursor(ref value);
						cursor.Bump([1, 2, 3]);
						return value;
					}

					// C# 14: null-conditional assignment
					public static string NullConditional(Settings? s) {
						s?.Retries = 9;
						return s?.Retries.ToString() ?? "none";
					}

					public static int Lambdas() {
						var add = (int a, int b = 2) => a + b;
						var count = (params int[] xs) => xs.Length;
						return add(1) + count(1, 2, 3);
					}

					public static string Extras() {
						Span<int> data = [1, 2, 3];
						int size = RefStructGenerics.Size(new SizedSpan(data));
						string overflow;
						try {
							overflow = checked(new Small(200) + new Small(100)).Value.ToString();
						}
						catch (OverflowException) {
							overflow = "overflow";
						}
						var wrapped = (new Small(200) + new Small(100)).Value;
						var counter = new Counter();
						counter += 5;
						counter++;
						counter.Add(Counter.Step.Five);
						var widget = new Widget("w");
						int changes = 0;
						widget.Changed += () => changes++;
						Pair pair = (size, 2);
						return $"{size} {overflow} {wrapped} {counter.Value} {widget.Rename("v")} {changes} {Pointers(4)} {Native(-3, 5)} {ReadOnlyRef(in size)} {nameof(List<>)} {pair.Left + pair.Right} {Unmatched("c")} {CountAsync(3).ToBlockingEnumerable().Sum()}";
					}

					// The compiler's helper throws SwitchExpressionException(object unmatchedValue)
					public static string Unmatched(string s) {
						try {
							return s switch { "a" => "A", "b" => "B" };
						}
						catch (SwitchExpressionException ex) {
							return $"unmatched:{ex.UnmatchedValue}:{ex.InnerException is null}";
						}
					}

					// C# 8: async streams
					public static async IAsyncEnumerable<int> CountAsync(int n) {
						for (int i = 1; i <= n; i++) {
							await Task.Yield();
							yield return i;
						}
					}

					// C# 9: function pointers, native integers; C# 12: ref readonly parameters
					static int Twice(int x) => x * 2;
					public static unsafe int Pointers(int x) {
						delegate*<int, int> f = &Twice;
						return f(x);
					}
					public static nint Native(nint a, nuint b) => a * (nint)b;
					public static int ReadOnlyRef(ref readonly int x) => x + 1;
				}

				public static class Program {
					public static string Run() {
						var r = new List<string>();
						var acc = new Account("ann", 10);
						acc.Deposit(5);
						var sav = new Savings("bob", 100, 0.1m);
						r.Add($"{acc.Owner} {acc.Balance} {acc.History} {sav.Interest()} {new Vector2(3, 4).Length}");
						r.Add(Features.InlineArrays() + " " + Features.RefFields());
						var settings = new Settings { Name = "n" };
						r.Add(settings.Name + settings.Retries + new Settings("m").Name);
						r.Add(GenericMath.Sum(new[] { new Money(1), new Money(2) }).ToString() + GenericMath.Max(3, 7) + Zero.Describe<Money>());
						r.Add(FileUser.Use().ToString());
						var t = new Temperature { Celsius = 21.27 };
						r.Add(t.Celsius + t.Unit);
						r.Add("abc".IsShort + "hey".Shout() + int.Triple(4));
						var log = new Logger();
						log.Write("a", "b");
						r.Add(log.Text);
						var profile = new Profile { Nick = "  x  " };
						r.Add(profile.Nick);
						r.Add(Features.Attributes());
						r.Add(string.Join(",", Features.Collections(3)));
						r.Add(Features.Patterns([]) + Features.Patterns([1]) + Features.Patterns([1, 2, 3]));
						r.Add(Features.NullConditional(new Settings { Name = "s" }) + Features.NullConditional(null));
						r.Add(Features.Lambdas().ToString());
						r.Add(Features.Extras());
						return string.Join(Environment.NewLine, r);
					}
				}
			}
			"""";
	}
}
