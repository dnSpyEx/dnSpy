using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace ConsoleSample;

public record Person(string First, string Last) {
	public string Full => $"{First} {Last}";
}

public readonly record struct Point(int X, int Y) {
	public static Point operator +(Point a, Point b) => new(a.X + b.X, a.Y + b.Y);
}

public class Counter(int start) {
	public int Value { get; private set; } = start;
	public void operator +=(int n) => Value += n;
	public void operator ++() => Value++;
}

public interface IShape {
	double Area { get; }
	static abstract IShape Unit();
}

public sealed class Square(double side) : IShape {
	public double Area => side * side;
	public static IShape Unit() => new Square(1);
}

public static class Extensions {
	extension(string s) {
		public string Shout() => s.ToUpperInvariant() + "!";
		public bool IsShort => s.Length < 4;
	}
}

public class Settings {
	public required string Name { get; init; }
	public string Trimmed {
		get => field ?? "";
		set => field = value.Trim();
	}
}

public static class Program {
	static IEnumerable<int> Evens(int max) {
		for (int i = 0; i <= max; i += 2)
			yield return i;
	}

	static async Task<int> SumAsync(IEnumerable<int> values) {
		int sum = 0;
		foreach (var v in values) {
			await Task.Yield();
			sum += v;
		}
		return sum;
	}

	static async IAsyncEnumerable<string> Words() {
		foreach (var w in new[] { "a", "bb", "ccc" }) {
			await Task.Delay(1);
			yield return w;
		}
	}

	static string Describe(object o) => o switch {
		int i when i > 10 => "big int",
		int => "int",
		string { Length: 0 } => "empty",
		string s => "string " + s,
		Person { First: "Ada" } p => "Ada " + p.Last,
		int[] and [var first, .., var last] => $"array {first}..{last}",
		null => "null",
		_ => "other",
	};

	static IReadOnlyList<int> Primes() => [2, 3, 5, 7, 11];
	static List<string> Names(IEnumerable<string> more) => ["x", .. more, "z"];
	static int Sum(params ReadOnlySpan<int> values) {
		int sum = 0;
		foreach (var v in values)
			sum += v;
		return sum;
	}

	public static async Task<int> Main() {
		var sb = new StringBuilder();
		var person = new Person("Ada", "Lovelace");
		sb.AppendLine(person.Full + " " + (person with { Last = "Byron" }));
		sb.AppendLine((new Point(1, 2) + new Point(3, 4)).ToString());
		var counter = new Counter(5);
		counter += 3;
		counter++;
		sb.AppendLine(counter.Value.ToString());
		sb.AppendLine(Square.Unit().Area.ToString());
		sb.AppendLine("hi".Shout() + " " + "hello".IsShort);
		var settings = new Settings { Name = "n", Trimmed = "  t  " };
		sb.AppendLine(settings.Name + settings.Trimmed + ".");
		sb.AppendLine(string.Join(",", Evens(8)));
		sb.AppendLine((await SumAsync(Evens(10))).ToString());
		await foreach (var w in Words())
			sb.Append(w).Append(' ');
		sb.AppendLine();
		foreach (var o in new object?[] { 42, 7, "", "s", person, new[] { 1, 2, 3 }, null, 1.5 })
			sb.Append(Describe(o)).Append(';');
		sb.AppendLine();
		sb.AppendLine(string.Join(",", Primes()) + " " + string.Join(",", Names(["y"])) + " " + Sum(1, 2, 3));
		var lookup = new[] { "apple", "avocado", "banana" }.GroupBy(a => a[0]).ToDictionary(g => g.Key, g => g.Count());
		sb.AppendLine(string.Join(",", lookup.Select(kv => $"{kv.Key}={kv.Value}")));
		Func<int, int, int> add = (a, b) => a + b;
		sb.AppendLine(add(2, 3).ToString());
		try {
			checked {
				int big = int.MaxValue;
				big += Environment.ProcessorCount;
			}
		}
		catch (OverflowException ex) when (ex.Message.Length > 0) {
			sb.AppendLine("overflow");
		}
		Console.Write(sb.ToString());
		return 0;
	}
}
