using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace NetFxSample {
	interface IAnimal {
		string Speak();
	}

	abstract class Animal : IAnimal {
		protected Animal(string name) => Name = name;
		public string Name { get; }
		public abstract string Speak();
		public override string ToString() => $"{GetType().Name}({Name})";
	}

	sealed class Dog : Animal {
		public Dog(string name) : base(name) { }
		public override string Speak() => "Woof";
	}

	sealed class Cat : Animal {
		public Cat(string name) : base(name) { }
		public override string Speak() => "Meow";
	}

	struct Vector {
		public readonly double X, Y;
		public Vector(double x, double y) { X = x; Y = y; }
		public static Vector operator *(Vector v, double f) => new Vector(v.X * f, v.Y * f);
		public double Length => Math.Sqrt(X * X + Y * Y);
	}

	static class Program {
		static event EventHandler<string> Message;

		static IEnumerable<string> Lines(string text) {
			using (var reader = new StringReader(text)) {
				string line;
				while ((line = reader.ReadLine()) != null)
					yield return line.Trim();
			}
		}

		static async Task<string> DownloadAsync(int n) {
			var sb = new StringBuilder();
			for (int i = 0; i < n; i++) {
				await Task.Delay(1).ConfigureAwait(false);
				sb.Append(i);
			}
			return sb.ToString();
		}

		static int Main() {
			var sb = new StringBuilder();
			Message += (s, m) => sb.AppendLine("event " + m);
			var animals = new List<Animal> { new Dog("Rex"), new Cat("Tom"), new Dog("Fido") };
			foreach (var group in animals.GroupBy(a => a.Speak()).OrderBy(g => g.Key))
				sb.AppendLine(group.Key + ": " + string.Join(", ", group.Select(a => a.ToString())));
			sb.AppendLine(string.Join("|", Lines(" a \n b\n  c  ")));
			sb.AppendLine(DownloadAsync(4).Result);
			sb.AppendLine((new Vector(3, 4) * 2).Length.ToString(System.Globalization.CultureInfo.InvariantCulture));
			var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { ["One"] = 1, ["two"] = 2 };
			sb.AppendLine(dict["ONE"] + dict["Two"] + " " + (dict.TryGetValue("three", out var three) ? three : -1));
			int? maybe = animals.Count > 2 ? (int?)animals.Count : null;
			sb.AppendLine((maybe ?? 0) + " " + (animals.FirstOrDefault(a => a.Name == "Tom")?.Name ?? "none"));
			Message?.Invoke(null, "done");
			switch (animals[0]) {
			case Dog d when d.Name.StartsWith("R"):
				sb.AppendLine("dog " + d.Name);
				break;
			case Cat c:
				sb.AppendLine("cat " + c.Name);
				break;
			}
			try {
				throw new InvalidOperationException("boom");
			}
			catch (InvalidOperationException ex) when (ex.Message == "boom") {
				sb.AppendLine("caught");
			}
			finally {
				sb.AppendLine("finally");
			}
			sb.AppendLine(typeof(System.Windows.Forms.Form).Name);
			Console.Write(sb.ToString());
			return 0;
		}
	}
}
