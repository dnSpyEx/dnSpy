// Scenarios for checking dnSpy's debugger on decompiled code. Each one is a case the upstream decompiler's debug
// info got wrong before dnSpy's adapter fixed it. See README.md for what to do and what should happen.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace DebuggerPlayground {
	public static class Program {
		public static async Task Main() {
			Console.WriteLine("1 switch: " + Scenarios.Switch(1) + " " + Scenarios.Switch(2) + " " + Scenarios.Switch(7));
			Console.WriteLine("2 break: " + Scenarios.Break(new[] { "ab", "", "c" }));
			Console.WriteLine("3 continue: " + Scenarios.Continue(new[] { 1, 20, 3 }));
			Console.WriteLine("4 do-while: " + Scenarios.DoWhile(5));
			var s = new Scenarios();
			s.Raise();
			s.Set(null);
			s.Set("x");
			Console.WriteLine("5 null-conditional: " + s.Value);
			Console.WriteLine("6 conditional: " + Scenarios.Conditional("", 1) + " " + Scenarios.Conditional("abc", 2));
			Console.WriteLine("7 catch: " + Scenarios.Catch("fail") + " " + Scenarios.Catch("ok"));
			Console.WriteLine("8 lambda: " + Scenarios.Captured(new List<int> { 3, 1, 2 }));
			s.Name = "name";
			Console.WriteLine("9 property: " + s.Name);
			Console.WriteLine("10 async: " + await Scenarios.ComputeAsync(3));
			try {
				await Scenarios.ThrowAsync();
			}
			catch (InvalidOperationException ex) {
				Console.WriteLine("11 async exception: " + ex.Message);
			}
			Console.WriteLine("12 lock/using/foreach: " + Scenarios.Resources(new object(), "line1\nline2"));
			try {
				Scenarios.Outer(3);
			}
			catch (ArgumentException ex) {
				Console.WriteLine("13 call stack: " + ex.Message);
			}
		}
	}

	public class Scenarios {
		public event EventHandler? Changed;
		object? value;
		public object? Value => value;
		public string Name { get; set; } = "";

		// 1. Breakpoint on 'return 10;' must stop only for value 1 (it stopped for the default case)
		public static int Switch(int value) {
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

		// 2. Breakpoint on 'break;' must stop only for the empty string (it stopped on every iteration)
		public static int Break(IEnumerable<string> names) {
			int total = 0;
			foreach (string name in names) {
				if (name.Length == 0)
					break;
				total += name.Length;
			}
			return total;
		}

		// 3. Breakpoint on 'sum += values[i];' must stop only for 1 and 3 (it stopped for 20)
		public static int Continue(int[] values) {
			int sum = 0;
			for (int i = 0; i < values.Length; i++) {
				if (values[i] > 10)
					continue;
				sum += values[i];
			}
			return sum;
		}

		// 4. Breakpoint on 'return n;' must stop once (it stopped on each loop iteration)
		public static int DoWhile(int n) {
			do {
				n--;
			} while (n > 0);
			return n;
		}

		// 5. Step over (F10) 'Changed?.Invoke(...)' and 'value = v ?? "default";' must not stop at '}' before the
		//    statement is done
		public void Raise() {
			Changed?.Invoke(this, EventArgs.Empty);
		}

		public void Set(object? v) {
			value = v ?? "default";
		}

		// 6. Breakpoint on the statement after the conditional must stop every time (it stopped only for one arm)
		public static string Conditional(string text, int n) {
			string result;
			if (text.Length == 0)
				result = "empty";
			else if (text.Length == 3 && n > 1)
				result = text.ToUpperInvariant();
			else
				result = text + n;
			Console.Write("");
			return result;
		}

		// 7. In the catch block, 'ex' must be in Locals and hovering it must show the exception
		public static string Catch(string text) {
			try {
				if (text == "fail")
					throw new IOException("io");
				return text;
			}
			catch (IOException ex) when (ex.HResult != 0) {
				return ex.Message;
			}
			finally {
				Console.Write("");
			}
		}

		// 8. Stopped on 'return f(...)', Watch 'captured' must show 3 (the captured variable)
		public static int Captured(List<int> list) {
			int captured = 3;
			Func<int, int> f = x => x * captured;
			list.Sort((a, b) => b.CompareTo(a));
			return f(list.Count);
		}

		// 9. A breakpoint on 'get;' and 'set;' of Name must work

		// 10. Step over each await (F10) must go to the next statement
		public static async Task<int> ComputeAsync(int x) {
			int a = await Task.FromResult(x);
			await Task.Delay(1);
			a += await OtherAsync(a);
			return a;
		}

		static async Task<int> OtherAsync(int v) {
			await Task.Yield();
			return v * 2;
		}

		// 11. Break when the exception is thrown: the frames of ThrowAsync/Inner must show their statements
		public static async Task ThrowAsync() {
			await Task.Yield();
			await InnerAsync();
		}

		static async Task InnerAsync() {
			await Task.Delay(1);
			throw new InvalidOperationException("async");
		}

		// 12. Stepping through lock/using/foreach, and breakpoints on their lines
		public static int Resources(object gate, string text) {
			int count = 0;
			lock (gate) {
				using var reader = new StringReader(text);
				string? line;
				while ((line = reader.ReadLine()) != null)
					count += line.Length;
			}
			foreach (var c in text)
				count += c == '\n' ? 1 : 0;
			return count;
		}

		// 13. Break when the exception is thrown: each frame (Outer, Middle) must highlight the call
		public static void Outer(int n) => Middle(n + 1);

		static void Middle(int n) {
			var list = new List<int> { n };
			list.ForEach(x => Inner(x));
		}

		static void Inner(int n) => throw new ArgumentException("thrown at " + n);
	}
}
