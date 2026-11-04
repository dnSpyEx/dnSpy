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
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	sealed class AssemblyResult {
		public string Path { get; set; } = "";
		public string Engine { get; set; } = "";
		public long Size { get; set; }
		public int StackMB { get; set; }
		public string? LoadError { get; set; }
		public string? BridgeKind { get; set; }
		public List<string> BridgeReasons { get; set; } = new List<string>();
		public double BridgeMs { get; set; }
		public string? BridgeError { get; set; }
		public int TotalTypes { get; set; }
		public int Types { get; set; }
		public int Ok { get; set; }
		public int Timeouts { get; set; }
		public List<string> TimeoutSamples { get; set; } = new List<string>();
		public Dictionary<string, int> Exceptions { get; set; } = new Dictionary<string, int>();
		public List<string> ExceptionSamples { get; set; } = new List<string>();
		public int DebugInfos { get; set; }
		public int DebugInfoErrors { get; set; }
		public int DebugInfoRepairs { get; set; }
		public List<string> DebugInfoRepairSamples { get; set; } = new List<string>();
		public List<string> DebugInfoErrorSamples { get; set; } = new List<string>();
		public double TotalMs { get; set; }
		public double MaxTypeMs { get; set; }
		public string? MaxType { get; set; }
		public List<double> TypeMs { get; set; } = new List<double>();
		public bool Crashed { get; set; }
		public string? CrashDetail { get; set; }
		public bool CrashedWithDefaultStack { get; set; }
		public string? DefaultStackCrashDetail { get; set; }

		public string ShortStatus() {
			if (Crashed)
				return "CRASH " + CrashDetail;
			if (LoadError is not null)
				return "dnlib can't load: " + LoadError;
			var s = $"{Ok}/{Types} ok, {Exceptions.Values.Sum()} exceptions, {Timeouts} timeouts, {DebugInfoErrors} debug info errors, {TotalMs / 1000:0.0}s";
			if (BridgeKind is not null)
				s += $", {BridgeKind}";
			if (CrashedWithDefaultStack)
				s += " (crashed with 1 MB stack: " + DefaultStackCrashDetail + ")";
			return s;
		}
	}

	/// <summary>
	/// Decompiles one assembly with one engine (runs in a child process)
	/// </summary>
	static class AssemblyRunner {
		static readonly TimeSpan typeTimeout = TimeSpan.FromSeconds(30);

		public static int Run(Options options) {
			var file = options.Paths.Single();
			var engine = options.Get("engine");
			int stackMB = int.Parse(options.Get("stack-mb"));
			int maxTypes = int.Parse(options.Get("max-types"));
			var result = new AssemblyResult { Path = file, Engine = engine, StackMB = stackMB, Size = new FileInfo(file).Length };
			var total = Stopwatch.StartNew();

			ModuleDefMD module;
			try {
				module = ModuleDefMD.Load(file, CreateModuleContext(file));
			}
			catch (Exception ex) {
				result.LoadError = $"{ex.GetType().Name}: {ex.Message}";
				return Write(options, result);
			}

			var allTypes = module.Types.Where(t => !t.IsGlobalModuleType).ToList();
			result.TotalTypes = allTypes.Count;
			var types = Sample(allTypes, maxTypes);
			var dump = OutputDump.Create(options.Get("dump", ""), file);

			UpstreamCSharpDecompiler? upstream = null;
			// "dnspy": the IDecompiler dnSpy uses (dnSpy's output, colors, references, braces, token comments)
			var language = engine == "dnspy" ? new UpstreamCSharpLanguage(new dnSpy.Decompiler.ILSpy.Core.Settings.UpstreamDecompilerSettings()) : null;
			if (engine == "upstream") {
				var cache = new ModuleBridgeCache(new ModuleBridgeOptions());
				try {
					var bridged = cache.Get(module);
					result.BridgeKind = bridged.ImageKind.ToString();
					result.BridgeReasons = bridged.NormalizeReasons.ToList();
					result.BridgeMs = bridged.CreationTime.TotalMilliseconds;
				}
				catch (Exception ex) {
					result.BridgeError = $"{ex.GetType().Name}: {ex.Message}";
					return Write(options, result);
				}
				upstream = new UpstreamCSharpDecompiler(cache, new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false });
			}

			// A failure of the statement shaper only loses its fixes in dnSpy: count it as an error here
			var shaperErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();
			dnSpy.Decompiler.ILSpy.Core.DebugInfo.StatementShaper.OnError = (method, ex) => shaperErrors.Enqueue($"{SafeName(method)}: statement shaper failed: {ex.GetType().Name}: {ex.Message}");

			foreach (var type in types) {
				result.Types++;
				var sw = Stopwatch.StartNew();
				IReadOnlyList<string> repairs = Array.Empty<string>();
				var outputErrors = new List<string>();
				while (shaperErrors.TryDequeue(out _)) { }
				var outcome = RunOnThread<(string text, IReadOnlyList<MethodDebugInfo> infos)>(stackMB, typeTimeout, token => {
					if (language is not null) {
						var output = new CapturingDecompilerOutput();
						language.Decompile(type, output, new DecompilationContext { CancellationToken = token, CalculateILSpans = true });
						outputErrors.AddRange(CheckOutput(output));
						return (output.Text, output.DebugInfos.ToArray());
					}
					if (upstream is null)
						throw new InvalidOperationException("Unknown engine: " + engine);
					var r = upstream.DecompileType(type, token);
					repairs = r.DebugInfoRepairs;
					return (r.Text, r.MethodDebugInfos);
				});
				double ms = sw.Elapsed.TotalMilliseconds;
				result.TypeMs.Add(Math.Round(ms, 1));
				if (ms > result.MaxTypeMs) {
					result.MaxTypeMs = ms;
					result.MaxType = SafeName(type);
				}
				dump?.Write(type, outcome.kind switch {
					OutcomeKind.Ok => outcome.value.text,
					OutcomeKind.Timeout => "// TIMEOUT\n",
					_ => $"// EXCEPTION: {outcome.exception!.GetType().Name}: {FirstLine(outcome.exception.Message)}\n",
				});
				switch (outcome.kind) {
				case OutcomeKind.Ok:
					result.Ok++;
					outputErrors.AddRange(shaperErrors);
					result.DebugInfoErrors += outputErrors.Count;
					foreach (var e in outputErrors.Take(1)) {
						if (result.DebugInfoErrorSamples.Count < 5)
							result.DebugInfoErrorSamples.Add($"{SafeName(type)}: output: {Truncate(e, 250)}");
					}
					result.DebugInfoRepairs += repairs.Count;
					foreach (var repair in repairs) {
						if (result.DebugInfoRepairSamples.Count < 5)
							result.DebugInfoRepairSamples.Add(Truncate(repair, 250));
					}
					foreach (var info in outcome.value.infos) {
						result.DebugInfos++;
						if (info.Method.Body is null)
							continue;
						var report = DebugInfoValidator.Validate(info, outcome.value.text, null);
						result.DebugInfoErrors += report.Errors.Count;
						foreach (var e in report.Errors.Take(1)) {
							if (result.DebugInfoErrorSamples.Count < 5)
								result.DebugInfoErrorSamples.Add($"{SafeName(info.Method)}: {e}");
						}
					}
					break;
				case OutcomeKind.Timeout:
					result.Timeouts++;
					if (result.TimeoutSamples.Count < 5)
						result.TimeoutSamples.Add(SafeName(type));
					break;
				case OutcomeKind.Exception:
					var key = outcome.exception!.GetType().Name;
					result.Exceptions[key] = result.Exceptions.TryGetValue(key, out int n) ? n + 1 : 1;
					if (result.ExceptionSamples.Count < 5)
						result.ExceptionSamples.Add($"{SafeName(type)}: {key}: {Truncate(outcome.exception.Message, 200)}");
					break;
				}
			}
			result.TotalMs = total.Elapsed.TotalMilliseconds;
			return Write(options, result);
		}

		/// <summary>
		/// What dnSpy's document viewer relies on: brace pairs at the braces, line separators and references
		/// inside the text, no upstream objects as references
		/// </summary>
		static IEnumerable<string> CheckOutput(CapturingDecompilerOutput output) {
			var text = output.Text;
			foreach (var pair in output.BracePairs) {
				if (pair.Left.End > text.Length || pair.Right.End > text.Length || pair.Left.End > pair.Right.Start) {
					yield return $"brace pair out of range {pair.Left} {pair.Right}";
					continue;
				}
				var expected = pair.Flags.ToBraceKind() switch {
					CodeBracesRangeFlags.BraceKind_CurlyBraces => "{}",
					CodeBracesRangeFlags.BraceKind_Parentheses => "()",
					CodeBracesRangeFlags.BraceKind_SquareBrackets => "[]",
					CodeBracesRangeFlags.BraceKind_AngleBrackets => "<>",
					CodeBracesRangeFlags.BraceKind_DoubleQuotes => "\"\"",
					CodeBracesRangeFlags.BraceKind_SingleQuotes => "''",
					_ => "??",
				};
				var actual = text.Substring(pair.Left.Start, pair.Left.Length) + text.Substring(pair.Right.Start, pair.Right.Length);
				if (actual != expected)
					yield return $"brace pair {pair.Flags} at '{actual}'";
			}
			foreach (var p in output.LineSeparators) {
				if (p < 0 || p >= text.Length || (text[p] != '}' && text[p] != ';'))
					yield return $"line separator at {p}";
			}
			foreach (var span in output.References) {
				if (span.Reference!.GetType().Namespace?.StartsWith("ICSharpCode", StringComparison.Ordinal) == true)
					yield return $"upstream object as reference: {span}";
			}
		}

		static int Write(Options options, AssemblyResult result) {
			File.WriteAllText(options.Get("out"), JsonSerializer.Serialize(result));
			return 0;
		}

		enum OutcomeKind { Ok, Exception, Timeout }

		/// <summary>
		/// Runs on a dedicated thread with a given stack size, like dnSpy should. A stack overflow can't be caught:
		/// it ends this (child) process and the parent records it.
		/// </summary>
		static (OutcomeKind kind, T value, Exception? exception) RunOnThread<T>(int stackMB, TimeSpan timeout, Func<CancellationToken, T> func) {
			using var cts = new CancellationTokenSource();
			T value = default!;
			Exception? exception = null;
			var thread = new Thread(() => {
				try {
					value = func(cts.Token);
				}
				catch (Exception ex) {
					exception = ex;
				}
			}, stackMB * 1024 * 1024) { IsBackground = true };
			thread.Start();
			if (!thread.Join(timeout)) {
				cts.Cancel();
				thread.Join(TimeSpan.FromSeconds(10));
				return (OutcomeKind.Timeout, default!, null);
			}
			if (exception is OperationCanceledException)
				return (OutcomeKind.Timeout, default!, null);
			return exception is null ? (OutcomeKind.Ok, value, null) : (OutcomeKind.Exception, default!, exception);
		}

		static List<TypeDef> Sample(List<TypeDef> types, int max) {
			if (types.Count <= max)
				return types;
			// Deterministic, spread over the whole module
			var result = new List<TypeDef>(max);
			double step = (double)types.Count / max;
			for (int i = 0; i < max; i++)
				result.Add(types[(int)(i * step)]);
			return result;
		}

		internal static ModuleContext CreateModuleContext(string file) {
			var context = new ModuleContext();
			var resolver = new AssemblyResolver(context) { FindExactMatch = false, EnableFrameworkRedirect = false };
			resolver.PreSearchPaths.Add(Path.GetDirectoryName(file)!);
			resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			return context;
		}

		static string SafeName(IFullName member) {
			var name = member.FullName;
			var chars = name.Select(c => char.IsControl(c) || char.IsSurrogate(c) ? '?' : c).ToArray();
			return Truncate(new string(chars), 150);
		}

		static string Truncate(string s, int max) => s.Length <= max ? s : s.Substring(0, max) + "...";

		static string FirstLine(string s) {
			int i = s.IndexOfAny(new[] { '\r', '\n' });
			return i < 0 ? s : s.Substring(0, i);
		}
	}
}
