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
using System.Runtime.InteropServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Edit Method / Edit Class without changes on real assemblies (opt-in: EDIT_CORPUS=directory with the assemblies
	/// and their references, EDIT_CORPUS_SAMPLES=methods per assembly). The code can't be run, so it measures how often
	/// the code compiles, imports and the module can be saved.
	/// </summary>
	public class EditCorpusTests {
		readonly ITestOutputHelper log;
		public EditCorpusTests(ITestOutputHelper log) => this.log = log;

		static ModuleDefMD Load(string file) {
			var context = new ModuleContext();
			var resolver = new AssemblyResolver(context) { FindExactMatch = false, EnableFrameworkRedirect = false };
			resolver.PreSearchPaths.Add(Path.GetDirectoryName(file)!);
			// Like dnSpy (AssemblyResolver: dotNetPathProvider.TryGetSharedDotNetPaths): a .NET assembly's framework
			// references come from the shared framework of its version, not the one running the tests. (With .NET 10's,
			// editing a .NET 8 assembly fails with CS1705: its references use a newer System.Runtime.)
			// netstandard 2.0 (and .NET Framework): dnSpy resolves from the GAC like .NET Framework 4 (without a .NET Core
			// below 2.2, AssemblyResolver.ResolveNormal: TryGetClosestNetStandardCompatibleVersion), not from .NET's
			// runtime (that one's assemblies and netstandard's were two core libraries: CS0518, CS0433). netstandard 2.1:
			// the newest .NET.
			if (GetSharedFrameworkDirectory(file) is string frameworkDir) {
				resolver.PreSearchPaths.Add(frameworkDir);
				resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			}
			else if (GetTargetFramework(file) is string tfm && tfm.StartsWith(".NETStandard,Version=v", StringComparison.Ordinal) &&
				Version.TryParse(tfm.Substring(".NETStandard,Version=v".Length), out var netStandard) && netStandard >= new Version(2, 1))
				resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			return ModuleDefMD.Load(file, context);
		}

		/// <summary>
		/// The TargetFrameworkAttribute's value, e.g. ".NETCoreApp,Version=v10.0"
		/// </summary>
		static string? GetTargetFramework(string file) {
			using var module = ModuleDefMD.Load(file);
			return module.Assembly?.CustomAttributes.Find("System.Runtime.Versioning.TargetFrameworkAttribute")?.ConstructorArguments.FirstOrDefault().Value?.ToString();
		}

		/// <summary>
		/// The installed Microsoft.NETCore.App of the assembly's target framework (.NETCoreApp,Version=vX.Y): the newest
		/// X.Y.*, else the oldest newer one (roll forward); null if it's not a .NET assembly
		/// </summary>
		static string? GetSharedFrameworkDirectory(string file) {
			var tfm = GetTargetFramework(file);
			const string prefix = ".NETCoreApp,Version=v";
			if (tfm is null || !tfm.StartsWith(prefix, StringComparison.Ordinal) || !Version.TryParse(tfm.Substring(prefix.Length), out var target))
				return null;
			var sharedDir = Path.GetDirectoryName(RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))!;
			var installed = Directory.GetDirectories(sharedDir)
				.Select(dir => (dir, version: Version.TryParse(Path.GetFileName(dir).Split('-')[0], out var v) ? v : null))
				.Where(a => a.version is not null).ToList();
			var sameMinor = installed.Where(a => a.version!.Major == target.Major && a.version.Minor == target.Minor).OrderByDescending(a => a.version).FirstOrDefault();
			if (sameMinor.dir is not null)
				return sameMinor.dir;
			return installed.Where(a => a.version! > target).OrderBy(a => a.version).FirstOrDefault().dir;
		}

		sealed class Stats {
			public int Total, Ok;
			public Dictionary<string, int> Categories = new Dictionary<string, int>();
			public Dictionary<string, int> Errors = new Dictionary<string, int>();
			public List<string> Examples = new List<string>();
			public List<double> EditSeconds = new List<double>();
			public double Seconds;

			public void Add(string category, IEnumerable<string> errors, string example, double seconds) {
				Total++;
				EditSeconds.Add(seconds);
				if (category == "ok") {
					Ok++;
					return;
				}
				Categories[category] = Categories.GetValueOrDefault(category) + 1;
				foreach (var e in errors.Distinct())
					Errors[e] = Errors.GetValueOrDefault(e) + 1;
				// EDIT_CORPUS_EXAMPLES: how many failed edits the report shows (default 8)
				if (Examples.Count < (int.TryParse(Environment.GetEnvironmentVariable("EDIT_CORPUS_EXAMPLES"), out int max) ? max : 8))
					Examples.Add(example);
			}
		}

		[Fact]
		public void EditMethodAndClass_RealWorldAssemblies() {
			var dir = Environment.GetEnvironmentVariable("EDIT_CORPUS");
			if (dir is null)
				return;
			int methodSamples = int.TryParse(Environment.GetEnvironmentVariable("EDIT_CORPUS_SAMPLES"), out int n) ? n : 30;
			int typeSamples = Math.Max(1, methodSamples / 4);
			var engines = new[] { "upstream" };
			// Optional file that gets each edit before it starts, to find the edit if the test host crashes
			var progress = Environment.GetEnvironmentVariable("EDIT_CORPUS_PROGRESS");
			// Optional: only these edits ("File.dll:0x06000001,File.dll:0x02000002"), e.g. to repeat one
			var only = Environment.GetEnvironmentVariable("EDIT_CORPUS_ONLY")?.Split(',').Select(a => a.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var totals = engines.ToDictionary(e => e, e => new Stats());
			foreach (var file in Directory.GetFiles(dir, "*.dll").OrderBy(a => a, StringComparer.Ordinal)) {
				var module = Load(file);
				var upstream = LanguageRunner.Create();
				// string.GetHashCode() is randomized per process: use a stable seed so a run can be repeated
				var random = new Random(StableHash(Path.GetFileName(file)));
				if (only is not null && !only.Any(a => a.StartsWith(Path.GetFileName(file) + ":", StringComparison.OrdinalIgnoreCase)))
					continue;
				var methods = module.GetTypes()
					.Where(t => !t.IsGlobalModuleType && IsVisible(upstream, t))
					.SelectMany(t => t.Methods.Where(m => m.HasBody && upstream.ShowMember(m)))
					.Select(m => m.MDToken.Raw).OrderBy(_ => random.Next()).Take(methodSamples).ToList();
				var types = module.Types.Where(t => !t.IsGlobalModuleType && upstream.ShowMember(t) && !t.Name.String.StartsWith("<", StringComparison.Ordinal))
					.Select(t => t.MDToken.Raw).OrderBy(_ => random.Next()).Take(typeSamples).ToList();
				if (only is not null) {
					// (Tokens that aren't in the sample)
					foreach (var a in only.Where(a => a.StartsWith(Path.GetFileName(file) + ":", StringComparison.OrdinalIgnoreCase))) {
						uint token = Convert.ToUInt32(a.Substring(a.IndexOf(':') + 1), 16);
						var list = (token >> 24) == 0x02 ? types : methods;
						if (!list.Contains(token))
							list.Add(token);
					}
				}
				foreach (var engine in engines) {
					var stats = new Stats();
					var watch = System.Diagnostics.Stopwatch.StartNew();
					foreach (var (kind, tokens) in new[] { (EditKind.EditMethod, methods), (EditKind.EditClass, types) }) {
						foreach (var token in tokens) {
							if (only is not null && !only.Contains($"{Path.GetFileName(file)}:0x{token:X8}"))
								continue;
							var decompiler = LanguageRunner.Create();
							// Optional: Edit Method's minimum image size to leave out the unused bodies (e.g. a big number: never)
							if (int.TryParse(Environment.GetEnvironmentVariable("EDIT_CORPUS_UNUSED_BODIES_MIN_SIZE"), out int minSize) && decompiler is UpstreamCSharpLanguage language)
								language.EditUnusedBodiesMinSize = minSize;
							if (progress is not null)
								File.AppendAllText(progress, $"{engine} {kind} {Path.GetFileName(file)} 0x{token:X8}{Environment.NewLine}");
							var (category, errors, example, editSeconds) = Edit(decompiler, kind, file, token);
							stats.Add(category, errors, example, editSeconds);
							totals[engine].Add(category, errors, example, editSeconds);
						}
					}
					stats.Seconds = watch.Elapsed.TotalSeconds;
					totals[engine].Seconds += stats.Seconds;
					Report($"{engine} {Path.GetFileName(file)}", stats, examples: false);
				}
			}
			foreach (var engine in engines)
				Report($"{engine} TOTAL", totals[engine], examples: true);
		}

		void Report(string title, Stats stats, bool examples) {
			var times = stats.EditSeconds.OrderBy(a => a).ToList();
			string timing = times.Count == 0 ? "" : $", per edit: median {times[times.Count / 2]:F1} s, max {times[^1]:F1} s";
			log.WriteLine($"{title}: {stats.Ok}/{stats.Total} OK ({stats.Seconds:F0} s{timing})" + (stats.Categories.Count == 0 ? "" : ", " +
				string.Join(", ", stats.Categories.OrderByDescending(a => a.Value).Select(a => $"{a.Key} {a.Value}"))));
			if (!examples)
				return;
			log.WriteLine("  most common errors (edits with them): " + string.Join(", ", stats.Errors.OrderByDescending(a => a.Value).Take(15).Select(a => $"{a.Key} {a.Value}")));
			foreach (var e in stats.Examples)
				log.WriteLine("  " + e);
		}

		/// <summary>
		/// Edits a member, returns the result and the time it took (decompile, compile, import, save; not the checks)
		/// </summary>
		static (string category, IEnumerable<string> errors, string example, double seconds) Edit(IDecompiler decompiler, EditKind kind, string file, uint token) {
			var editWatch = System.Diagnostics.Stopwatch.StartNew();
			var (category, errors, example) = Edit(decompiler, kind, file, token, editWatch);
			// (The token, to repeat it with EDIT_CORPUS_ONLY)
			return (category, errors, $"{Path.GetFileName(file)}:0x{token:X8} {example}", editWatch.Elapsed.TotalSeconds);
		}

		static (string category, IEnumerable<string> errors, string example) Edit(IDecompiler decompiler, EditKind kind, string file, uint token, System.Diagnostics.Stopwatch editWatch) {
			var module = Load(file);
			try {
				var member = (IMemberDef)module.ResolveToken(token);
				var watch = System.Diagnostics.Stopwatch.StartNew();
				var result = EditPipeline.Decompile(decompiler, kind, module, member);
				var decompileTime = watch.Elapsed;
				if (Environment.GetEnvironmentVariable("EDIT_CORPUS_DUMP") is string dumpDir) {
					Directory.CreateDirectory(dumpDir);
					foreach (var doc in result.Documents)
						File.WriteAllText(Path.Combine(dumpDir, $"{Path.GetFileNameWithoutExtension(file)}-{token:X8}-{doc.Name}"), doc.Text);
				}
				if (result.DecompileError is not null)
					return ("decompile", new[] { "decompile" }, result.Describe());
				watch.Restart();
				bool imported = EditPipeline.CompileAndImport(result, module, member);
				if (Environment.GetEnvironmentVariable("EDIT_CORPUS_STAGES") is string stagesFile)
					File.AppendAllText(stagesFile, $"{kind} {result.Target}: decompile {decompileTime.TotalSeconds:F1} s, compile+import {watch.Elapsed.TotalSeconds:F1} s, documents {string.Join("+", result.Documents.Select(a => a.Text.Length))} chars{Environment.NewLine}");
				if (!imported) {
					if (result.CompileErrors.Count != 0)
						return ("compile", result.CompileErrors.Select(a => a.Id), result.Describe(3));
					return ("import", result.ImportErrors.Select(a => a.Split(' ')[0]), result.Describe(3));
				}
				var image = new MemoryStream();
				try {
					module.Write(image);
				}
				catch (Exception ex) {
					return ("write", new[] { "write" }, $"{kind} {result.Target}: {ex.Message}");
				}
				editWatch.Stop();
				// Optional: the edited modules, to compare with the originals
				if (Environment.GetEnvironmentVariable("EDIT_CORPUS_SAVE") is string saveDir) {
					Directory.CreateDirectory(saveDir);
					File.WriteAllBytes(Path.Combine(saveDir, $"{Path.GetFileNameWithoutExtension(file)}-{token:X8}.dll"), image.ToArray());
				}
				// The edited type keeps its nullable annotations
				var topLevelType = member is TypeDef type ? type : member.DeclaringType;
				while (topLevelType.DeclaringType is not null)
					topLevelType = topLevelType.DeclaringType;
				var differences = NullabilityOracle.Compare(File.ReadAllBytes(file), image.ToArray(), topLevelType.FullName);
				if (differences.Count != 0)
					return ("nullability", new[] { "nullability" }, $"{kind} {result.Target}: " + string.Join(" | ", differences.Take(3)));
				return ("ok", Array.Empty<string>(), "");
			}
			catch (Exception ex) {
				return ("exception", new[] { ex.GetType().Name }, $"{kind} 0x{token:X8}: {ex}");
			}
			finally {
				(decompiler as UpstreamCSharpLanguage)?.OnAllModulesRemoved();
				module.Dispose();
			}
		}

		static int StableHash(string s) {
			uint hash = 2166136261;
			foreach (var c in s)
				hash = (hash ^ c) * 16777619;
			return (int)(hash & 0x7FFFFFFF);
		}

		static bool IsVisible(IDecompiler decompiler, TypeDef type) {
			for (var t = type; t is not null; t = t.DeclaringType) {
				if (!decompiler.ShowMember(t))
					return false;
			}
			return true;
		}
	}
}
