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
using System.Text.Json;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// Compares a run (the head, e.g. a pull request that updates the decompiler) with another one (the base) on the
	/// same corpus: what got worse fails the comparison, and the types whose output changed are listed for review.
	/// Each run's directory has results.json and, if it was run with --dump DIR/dump, the output of each type.
	/// </summary>
	static class Compare {
		public static int Run(Options options) {
			var headDir = Path.GetFullPath(options.Get("head"));
			var baseOption = options.Get("base", "");
			var baseDir = baseOption.Length == 0 ? null : Path.GetFullPath(baseOption);
			var title = options.Get("title", "Decompiler corpus");
			var head = Load(headDir) ?? throw new ArgumentException("No results.json in " + headDir);
			var baseResults = baseDir is null ? null : Load(baseDir);

			var regressions = new List<string>();
			var notes = new List<string>();
			var sb = new StringBuilder();
			var rows = new StringBuilder();
			var changes = new StringBuilder();
			int changedTypes = 0, comparedTypes = 0;

			var baseByKey = baseResults?.ToDictionary(Key, StringComparer.OrdinalIgnoreCase);
			foreach (var h in head.OrderBy(Key, StringComparer.OrdinalIgnoreCase)) {
				var name = Path.GetFileName(h.Path);
				AssemblyResult? b = null;
				baseByKey?.TryGetValue(Key(h), out b);
				if (baseResults is not null && b is null)
					notes.Add($"{name}: not in the base run");

				if (b is null) {
					if (h.Crashed)
						regressions.Add($"{name}: crashed ({h.CrashDetail})");
				}
				else {
					if (h.Crashed && !b.Crashed)
						regressions.Add($"{name}: crashed ({h.CrashDetail})");
					if (h.LoadError is not null && b.LoadError is null)
						regressions.Add($"{name}: dnlib can't load it ({h.LoadError})");
					if (h.Ok < b.Ok)
						regressions.Add($"{name}: {b.Ok - h.Ok} fewer types decompiled ({b.Ok} → {h.Ok})");
					if (Exceptions(h) > Exceptions(b))
						regressions.Add($"{name}: more exceptions ({Exceptions(b)} → {Exceptions(h)}): {string.Join("; ", h.ExceptionSamples.Take(3))}");
					if (h.Timeouts > b.Timeouts)
						regressions.Add($"{name}: more timeouts ({b.Timeouts} → {h.Timeouts}): {string.Join("; ", h.TimeoutSamples.Take(3))}");
					if (h.DebugInfoErrors > b.DebugInfoErrors)
						regressions.Add($"{name}: more debug info errors ({b.DebugInfoErrors} → {h.DebugInfoErrors}): {string.Join("; ", h.DebugInfoErrorSamples.Take(3))}");
					if (h.CrashedWithDefaultStack && !b.CrashedWithDefaultStack)
						notes.Add($"{name}: overflows a 1 MB stack now (dnSpy decompiles on big-stack threads)");
				}

				string output = "";
				var headDump = Path.Combine(headDir, "dump", name);
				var baseDump = baseDir is null ? null : Path.Combine(baseDir, "dump", name);
				if (Directory.Exists(headDump) && baseDump is not null && Directory.Exists(baseDump)) {
					var diff = CompareDumps(baseDump, headDump);
					comparedTypes += diff.total;
					changedTypes += diff.changed.Count + diff.added.Count + diff.removed.Count;
					int count = diff.changed.Count + diff.added.Count + diff.removed.Count;
					output = count == 0 ? $"same ({diff.total} types)" : $"**{count} of {diff.total} types changed**";
					if (count != 0) {
						changes.AppendLine($"<details><summary>{name}: {count} types</summary>");
						changes.AppendLine();
						foreach (var (kind, files) in new[] { ("changed", diff.changed), ("new", diff.added), ("gone", diff.removed) }) {
							foreach (var f in files.Take(50))
								changes.AppendLine($"- {kind}: `{f}`");
							if (files.Count > 50)
								changes.AppendLine($"- ... {files.Count - 50} more {kind}");
						}
						changes.AppendLine();
						changes.AppendLine("</details>");
					}
				}

				rows.AppendLine($"| {name} | {Change(b?.Ok, h.Ok)} / {h.Types} | {Change(b is null ? null : Exceptions(b), Exceptions(h))} | {Change(b?.Timeouts, h.Timeouts)} | " +
					$"{Change(b?.DebugInfoErrors, h.DebugInfoErrors)} | {(h.Crashed ? "**crash**" : h.LoadError is not null ? "can't load" : "")} | {output} |");
			}

			sb.AppendLine($"## {title}");
			sb.AppendLine();
			if (baseResults is null)
				sb.AppendLine(baseDir is null ? "No base run to compare with: only crashes fail." : "The base run has no results (its corpus runner is older): only crashes fail.");
			sb.AppendLine(regressions.Count == 0 ? "**No regressions.**" : $"**{regressions.Count} regressions:**");
			foreach (var r in regressions)
				sb.AppendLine($"- {r}");
			sb.AppendLine();
			if (baseResults is not null && comparedTypes == 0 && Directory.Exists(Path.Combine(headDir, "dump")))
				sb.AppendLine("The base run has no output dump (its corpus runner is older): the output isn't compared.");
			if (comparedTypes != 0)
				sb.AppendLine(changedTypes == 0 ? $"The output of all {comparedTypes} types is the same." : $"The output of {changedTypes} of {comparedTypes} types changed: review the diff (output.diff in the artifact).");
			sb.AppendLine();
			sb.AppendLine("| Assembly | Types decompiled | Exceptions | Timeouts | Debug info errors | | Output |");
			sb.AppendLine("|---|---|---|---|---|---|---|");
			sb.Append(rows);
			sb.AppendLine();
			sb.Append(changes);
			if (notes.Count != 0) {
				sb.AppendLine();
				foreach (var n in notes)
					sb.AppendLine($"- {n}");
			}
			File.WriteAllText(options.Get("out"), sb.ToString());
			Console.WriteLine(sb.ToString());
			return regressions.Count == 0 ? 0 : 1;
		}

		static string Key(AssemblyResult r) => Path.GetFileName(r.Path) + "|" + r.Engine;

		static int Exceptions(AssemblyResult r) => r.Exceptions.Values.Sum();

		static string Change(int? before, int after) => before is null || before == after ? after.ToString() : $"{before} → {after}";

		static List<AssemblyResult>? Load(string dir) {
			var file = Path.Combine(dir, "results.json");
			return File.Exists(file) ? JsonSerializer.Deserialize<List<AssemblyResult>>(File.ReadAllText(file)) : null;
		}

		static (int total, List<string> changed, List<string> added, List<string> removed) CompareDumps(string baseDir, string headDir) {
			var baseFiles = Directory.GetFiles(baseDir).Select(a => Path.GetFileName(a)).ToHashSet(StringComparer.Ordinal);
			var headFiles = Directory.GetFiles(headDir).Select(a => Path.GetFileName(a)).ToHashSet(StringComparer.Ordinal);
			var changed = new List<string>();
			foreach (var f in headFiles.Intersect(baseFiles).OrderBy(a => a, StringComparer.Ordinal)) {
				if (!File.ReadAllBytes(Path.Combine(baseDir, f)).AsSpan().SequenceEqual(File.ReadAllBytes(Path.Combine(headDir, f))))
					changed.Add(f);
			}
			var added = headFiles.Except(baseFiles).OrderBy(a => a, StringComparer.Ordinal).ToList();
			var removed = baseFiles.Except(headFiles).OrderBy(a => a, StringComparer.Ordinal).ToList();
			return (headFiles.Union(baseFiles).Count(), changed, added, removed);
		}
	}
}
