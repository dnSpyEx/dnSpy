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
using System.IO;
using System.Linq;
using System.Text;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	static class Report {
		public static string Create(List<AssemblyResult> results, List<string> roots) {
			var sb = new StringBuilder();
			sb.AppendLine("# Decompiler corpus report");
			sb.AppendLine();
			foreach (var root in roots.Select(Path.GetFullPath)) {
				var group = results.Where(r => r.Path.StartsWith(root, StringComparison.Ordinal)).ToList();
				if (group.Count == 0)
					continue;
				sb.AppendLine($"## {root}");
				sb.AppendLine();
				sb.AppendLine("| | " + string.Join(" | ", Engines(group)) + " |");
				sb.AppendLine("|---|" + string.Join("|", Engines(group).Select(_ => "---")) + "|");
				Row(sb, group, "Assemblies", g => g.Count.ToString());
				Row(sb, group, "dnlib can't load", g => g.Count(r => r.LoadError is not null).ToString());
				Row(sb, group, "Process crashes (1 MB stack)", g => g.Count(r => r.Crashed || r.CrashedWithDefaultStack).ToString());
				Row(sb, group, "Process crashes (256 MB stack)", g => g.Count(r => r.Crashed).ToString());
				Row(sb, group, "Original image / normalized", g => g.Any(r => r.BridgeKind is not null) ?
					$"{g.Count(r => r.BridgeKind == "Original")} / {g.Count(r => r.BridgeKind == "Normalized")}" : "");
				Row(sb, group, "Bridge failures", g => g.Any(r => r.BridgeKind is not null || r.BridgeError is not null) ? g.Count(r => r.BridgeError is not null).ToString() : "");
				Row(sb, group, "Types decompiled", g => $"{g.Sum(r => r.Ok)} / {g.Sum(r => r.Types)} ({Percent(g.Sum(r => r.Ok), g.Sum(r => r.Types))})");
				Row(sb, group, "Types failing (exception)", g => g.Sum(r => r.Exceptions.Values.Sum()).ToString());
				Row(sb, group, "Types timing out (30 s)", g => g.Sum(r => r.Timeouts).ToString());
				Row(sb, group, "Debug info: methods / invariant errors", g => $"{g.Sum(r => r.DebugInfos)} / {g.Sum(r => r.DebugInfoErrors)}");
				Row(sb, group, "Invalid upstream sequence points repaired", g => g.Any(r => r.Engine == "upstream") ? g.Sum(r => r.DebugInfoRepairs).ToString() : "");
				Row(sb, group, "Time per type: median / p95 / max (ms)", g => {
					var all = g.SelectMany(r => r.TypeMs).OrderBy(x => x).ToList();
					return all.Count == 0 ? "" : $"{P(all, 0.5):0} / {P(all, 0.95):0} / {all[all.Count - 1]:0}";
				});
				Row(sb, group, "Bridge time: median / max (ms)", g => {
					var all = g.Where(r => r.BridgeKind is not null).Select(r => r.BridgeMs).OrderBy(x => x).ToList();
					return all.Count == 0 ? "" : $"{P(all, 0.5):0} / {all[all.Count - 1]:0}";
				});
				sb.AppendLine();

				var reasons = group.SelectMany(r => r.BridgeReasons).Select(Generalize).GroupBy(r => r).OrderByDescending(g => g.Count()).ToList();
				if (reasons.Count != 0) {
					sb.AppendLine("Why modules were normalized:");
					foreach (var r in reasons.Take(10))
						sb.AppendLine($"- {r.Key} ({r.Count()})");
					sb.AppendLine();
				}

				foreach (var engine in Engines(group)) {
					var exceptions = group.Where(r => r.Engine == engine).SelectMany(r => r.Exceptions).GroupBy(kv => kv.Key)
						.Select(g => (name: g.Key, count: g.Sum(kv => kv.Value))).OrderByDescending(x => x.count).ToList();
					if (exceptions.Count != 0)
						sb.AppendLine($"{engine} exceptions: " + string.Join(", ", exceptions.Select(e => $"{e.name} ×{e.count}")));
				}
				sb.AppendLine();

				var problems = group.Where(r => r.Crashed || r.CrashedWithDefaultStack || r.BridgeError is not null || r.Timeouts != 0 || r.Exceptions.Count != 0 || r.DebugInfoErrors != 0 || r.DebugInfoRepairs != 0).ToList();
				if (problems.Count != 0) {
					sb.AppendLine("<details><summary>Assemblies with problems</summary>");
					sb.AppendLine();
					foreach (var r in problems) {
						sb.AppendLine($"- **{Path.GetFileName(r.Path)}** ({r.Engine}): {r.ShortStatus()}");
						foreach (var s in r.ExceptionSamples.Concat(r.TimeoutSamples.Select(t => "timeout: " + t)).Concat(r.DebugInfoErrorSamples).Concat(r.DebugInfoRepairSamples.Select(x => "repaired: " + x)).Take(5))
							sb.AppendLine($"  - `{s.Replace('`', '\'')}`");
						if (r.BridgeError is not null)
							sb.AppendLine($"  - bridge: `{r.BridgeError}`");
					}
					sb.AppendLine();
					sb.AppendLine("</details>");
					sb.AppendLine();
				}
			}
			return sb.ToString();
		}

		static IEnumerable<string> Engines(List<AssemblyResult> group) => group.Select(r => r.Engine).Distinct().OrderByDescending(e => e == "upstream");

		static void Row(StringBuilder sb, List<AssemblyResult> group, string name, Func<List<AssemblyResult>, string> value) =>
			sb.AppendLine($"| {name} | " + string.Join(" | ", Engines(group).Select(e => value(group.Where(r => r.Engine == e).ToList()))) + " |");

		static string Generalize(string reason) {
			int colon = reason.IndexOf(':');
			var s = colon > 0 && !reason.StartsWith("Duplicate") && !reason.StartsWith("Non-standard") ? reason.Substring(0, colon) : reason;
			return System.Text.RegularExpressions.Regex.Replace(s, @"0x[0-9A-F]{8}", "0x…");
		}

		static double P(List<double> sorted, double p) => sorted[Math.Min(sorted.Count - 1, (int)(p * sorted.Count))];
		static string Percent(int a, int b) => b == 0 ? "n/a" : $"{100.0 * a / b:0.0}%";
	}
}
