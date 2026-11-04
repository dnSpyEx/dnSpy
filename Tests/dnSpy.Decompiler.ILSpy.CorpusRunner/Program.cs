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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// Usage:
	///   run --out DIR [--engines upstream,dnspy] [--max-types N] [--jobs N] [--dump DIR] PATH...
	///       Decompiles every assembly under PATH (files or directories) in child processes and writes
	///       DIR/results.json and DIR/report.md. --dump writes each type's output to DIR/ASSEMBLY/TYPE.cs (one engine).
	///   one --engine upstream|dnspy --stack-mb N --max-types N --out FILE [--dump DIR] ASSEMBLY
	///       (child process) Decompiles one assembly and writes FILE
	///   compare --head DIR [--base DIR] --out FILE
	///       Compares two runs (results and dumps) and writes a Markdown summary. Exit code 1 if the head run is worse
	///       (crashes, load errors, exceptions, timeouts, fewer types decompiled, more debug info errors), or without
	///       a base run, if it has crashes.
	///   generate --out DIR BASE_ASSEMBLY
	///       Writes synthetic samples with obfuscator/anti-decompiler tricks
	///   inspect --type FULLNAME [--method NAME] [--text true] ASSEMBLY
	///       Prints a method's IL and the debug info statements the decompiler produced for it
	/// </summary>
	static class Program {
		static int Main(string[] args) {
			try {
				if (args.Length == 0) {
					Console.Error.WriteLine("Usage: run|one|compare|generate|inspect ...");
					return 1;
				}
				var options = Options.Parse(args.Skip(1).ToArray());
				return args[0] switch {
					"run" => Run(options),
					"one" => AssemblyRunner.Run(options),
					"generate" => SampleGenerator.Generate(options.Get("out"), options.Paths.Single()),
					"inspect" => Inspect.Run(options),
					"compare" => Compare.Run(options),
					_ => throw new ArgumentException($"Unknown command '{args[0]}'"),
				};
			}
			catch (Exception ex) {
				Console.Error.WriteLine(ex);
				return 2;
			}
		}

		const int DefaultStackMB = 1;
		const int BigStackMB = 256;

		static int Run(Options options) {
			var outDir = Path.GetFullPath(options.Get("out"));
			Directory.CreateDirectory(outDir);
			var engines = options.Get("engines", "upstream").Split(',', StringSplitOptions.RemoveEmptyEntries);
			int maxTypes = int.Parse(options.Get("max-types", "250"));
			int jobs = int.Parse(options.Get("jobs", Environment.ProcessorCount.ToString()));
			var dump = options.Get("dump", "");
			if (dump.Length != 0 && engines.Length != 1)
				throw new ArgumentException("--dump needs one engine");
			var files = options.Paths.SelectMany(EnumerateAssemblies).Distinct(StringComparer.Ordinal).OrderBy(a => a, StringComparer.Ordinal).ToList();
			Console.WriteLine($"{files.Count} assemblies, engines: {string.Join(", ", engines)}, max types per assembly: {maxTypes}");

			var results = new List<AssemblyResult>();
			var lockObj = new object();
			int done = 0;
			var work = files.SelectMany(f => engines.Select(e => (file: f, engine: e))).ToList();
			Parallel.ForEach(work, new ParallelOptions { MaxDegreeOfParallelism = jobs }, item => {
				var result = RunChild(item.file, item.engine, DefaultStackMB, maxTypes, outDir, dump);
				if (result.Crashed) {
					// Can't be caught in-process (e.g. stack overflow): retry with a big stack to see if that's enough
					var retry = RunChild(item.file, item.engine, BigStackMB, maxTypes, outDir, dump);
					retry.CrashedWithDefaultStack = true;
					retry.DefaultStackCrashDetail = result.CrashDetail;
					result = retry;
				}
				lock (lockObj) {
					results.Add(result);
					done++;
					Console.WriteLine($"[{done}/{work.Count}] {item.engine,-8} {Path.GetFileName(item.file)}: {result.ShortStatus()}");
				}
			});

			results = results.OrderBy(r => r.Path, StringComparer.Ordinal).ThenBy(r => r.Engine, StringComparer.Ordinal).ToList();
			File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
			var report = Report.Create(results, options.Paths);
			File.WriteAllText(Path.Combine(outDir, "report.md"), report);
			Console.WriteLine(report);
			return 0;
		}

		static AssemblyResult RunChild(string file, string engine, int stackMB, int maxTypes, string outDir, string dump) {
			var resultFile = Path.Combine(outDir, "tmp", Guid.NewGuid().ToString("N") + ".json");
			Directory.CreateDirectory(Path.GetDirectoryName(resultFile)!);
			var psi = new ProcessStartInfo("dotnet") {
				RedirectStandardError = true,
				RedirectStandardOutput = true,
				UseShellExecute = false,
			};
			psi.ArgumentList.Add(typeof(Program).Assembly.Location);
			foreach (var a in new[] { "one", "--engine", engine, "--stack-mb", stackMB.ToString(), "--max-types", maxTypes.ToString(), "--out", resultFile })
				psi.ArgumentList.Add(a);
			if (dump.Length != 0) {
				psi.ArgumentList.Add("--dump");
				psi.ArgumentList.Add(Path.GetFullPath(dump));
			}
			psi.ArgumentList.Add(file);
			using var process = Process.Start(psi)!;
			var stderr = new StringBuilder();
			process.ErrorDataReceived += (s, e) => { if (e.Data is not null) lock (stderr) stderr.AppendLine(e.Data); };
			process.OutputDataReceived += (s, e) => { };
			process.BeginErrorReadLine();
			process.BeginOutputReadLine();
			bool exited = process.WaitForExit((int)TimeSpan.FromMinutes(20).TotalMilliseconds);
			if (!exited) {
				try { process.Kill(entireProcessTree: true); } catch { }
			}
			process.WaitForExit();
			if (exited && process.ExitCode == 0 && File.Exists(resultFile)) {
				var result = JsonSerializer.Deserialize<AssemblyResult>(File.ReadAllText(resultFile))!;
				File.Delete(resultFile);
				return result;
			}
			string err;
			lock (stderr)
				err = stderr.ToString();
			var lines = err.Split('\n', StringSplitOptions.RemoveEmptyEntries);
			return new AssemblyResult {
				Path = file,
				Engine = engine,
				StackMB = stackMB,
				Crashed = true,
				CrashDetail = !exited ? "Hung (killed after 20 minutes)" :
					$"Exit code {process.ExitCode}: " + string.Join(" | ", lines.Where(l => l.Contains("overflow", StringComparison.OrdinalIgnoreCase) || l.Contains("Exception", StringComparison.Ordinal)).Take(3).DefaultIfEmpty(lines.LastOrDefault() ?? "")),
			};
		}

		static IEnumerable<string> EnumerateAssemblies(string path) {
			path = Path.GetFullPath(path);
			if (File.Exists(path)) {
				yield return path;
				yield break;
			}
			foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) {
				var ext = Path.GetExtension(file);
				if (ext.Equals(".dll", StringComparison.OrdinalIgnoreCase) || ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
					yield return file;
			}
		}
	}

	sealed class Options {
		readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.Ordinal);
		public List<string> Paths { get; } = new List<string>();

		public static Options Parse(string[] args) {
			var options = new Options();
			for (int i = 0; i < args.Length; i++) {
				if (args[i].StartsWith("--", StringComparison.Ordinal))
					options.values[args[i].Substring(2)] = args[++i];
				else
					options.Paths.Add(args[i]);
			}
			return options;
		}

		public string Get(string name) => values.TryGetValue(name, out var v) ? v : throw new ArgumentException($"Missing --{name}");
		public string Get(string name, string defaultValue) => values.TryGetValue(name, out var v) ? v : defaultValue;
	}
}
