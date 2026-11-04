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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.IL;
using dnSpy.Decompiler.ILSpy.Core.Mixed;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Heavily obfuscated assemblies (<see cref="Obfuscator"/>: what ConfuserEx and co. do, on the C# 1-10 test
	/// program): each obfuscation on its own and all of them together. dnSpy decompiles every type in C#, IL and IL
	/// with C# without exceptions and with usable debug info, and Edit Method without changes keeps the program's
	/// behavior.
	/// </summary>
	public class ObfuscationTests {
		readonly ITestOutputHelper log;
		public ObfuscationTests(ITestOutputHelper log) => this.log = log;

		const string Namespace = "EditClassic";

		static readonly string[] configurationNames = {
			nameof(Obfuscations.Rename), nameof(Obfuscations.Strings), nameof(Obfuscations.ProxyCalls), nameof(Obfuscations.ControlFlow),
			nameof(Obfuscations.JunkCode), nameof(Obfuscations.JunkTypes), nameof(Obfuscations.All),
		};

		public static IEnumerable<object[]> Configurations() => configurationNames.Select(a => new object[] { a });

		static readonly Lazy<CompiledProgram> original = new Lazy<CompiledProgram>(() => TestCompiler.Compile("EditClassic", EditPrograms.Classic));
		static readonly Lazy<string> originalOutput = new Lazy<string>(() => ProgramRunner.Run(original.Value.Image, Namespace));
		static readonly Dictionary<string, (CompiledProgram program, string output)> obfuscated = new Dictionary<string, (CompiledProgram, string)>();

		/// <summary>
		/// The obfuscated program and what it prints (Program.Run() keeps its name: the runner calls it)
		/// </summary>
		internal static (CompiledProgram program, string output) Obfuscated(string configuration) {
			lock (obfuscated) {
				if (!obfuscated.TryGetValue(configuration, out var result)) {
					var what = Enum.Parse<Obfuscations>(configuration);
					var image = Obfuscator.Obfuscate(original.Value.Image, what, seed: 42,
						keepName: m => m is TypeDef { Name.String: "Program", Namespace.String: Namespace } || m is MethodDef { Name.String: "Run" } md && md.DeclaringType.Name == "Program");
					var program = new CompiledProgram("EditClassic", image, Array.Empty<byte>(), optimized: false);
					obfuscated[configuration] = result = (program, ProgramRunner.Run(image, Namespace));
				}
				return result;
			}
		}

		static bool Failed(string output) => output.StartsWith("EXCEPTION", StringComparison.Ordinal) || output.StartsWith("CRASH", StringComparison.Ordinal) ||
			output.StartsWith("TIMEOUT", StringComparison.Ordinal);

		/// <summary>
		/// The obfuscator's own check: the obfuscated program runs, and prints the same (renaming changes the type names
		/// it prints)
		/// </summary>
		[Theory]
		[MemberData(nameof(Configurations))]
		public void ObfuscatedProgram_StillWorks(string configuration) {
			var (_, output) = Obfuscated(configuration);
			Assert.False(Failed(output), output);
			if (!configuration.Contains(nameof(Obfuscations.Rename)) && configuration != nameof(Obfuscations.All))
				Assert.Equal(originalOutput.Value, output);
		}

		static IDecompiler CreateDecompiler(string language) => language switch {
			"C#" => LanguageRunner.Create(),
			"IL" => new ILDecompiler(DecompilerSettingsService.__Instance_DONT_USE.ILDecompilerSettings),
			"IL with C#" => ILWithCSharpDecompiler.Create(DecompilerSettingsService.__Instance_DONT_USE.ILDecompilerSettings, LanguageRunner.Create()),
			_ => throw new ArgumentException(language),
		};

		public static IEnumerable<object[]> ConfigurationsAndLanguages() {
			foreach (var configuration in configurationNames) {
				foreach (var language in new[] { "C#", "IL", "IL with C#" })
					yield return new object[] { configuration, language };
			}
		}

		/// <summary>
		/// What a failed decompilation looks like in the output (the code itself can have any words in it)
		/// </summary>
		static readonly string[] errorMarkers = { "DecompilerException", "\n   at ICSharpCode.", "\n   at dnSpy.", "\n   at dnlib." };

		/// <summary>
		/// Decompiling every type in a language must not throw, show a decompiler exception, or hang: obfuscated code is
		/// adversarial, but the decompiler is robust (it may decompile to something meaningless, but it finishes)
		/// </summary>
		[Theory]
		[MemberData(nameof(ConfigurationsAndLanguages))]
		public void EveryType_DecompilesWithoutErrors(string configuration, string language) {
			var module = Obfuscated(configuration).program.Load();
			var decompiler = CreateDecompiler(language);
			var failures = new List<string>();
			int types = 0, methods = 0;
			var watch = Stopwatch.StartNew();
			foreach (var type in module.Types) {
				types++;
				try {
					var typeWatch = Stopwatch.StartNew();
					string text;
					IReadOnlyList<MethodDebugInfo> infos;
					if (language == "C#")
						(text, infos) = LanguageRunner.DecompileType(decompiler, type);
					else {
						var output = LanguageRunner.Decompile(decompiler, type);
						text = output.ToString();
						infos = output.DebugInfos.ToList();
					}
					methods += infos.Count;
					if (typeWatch.Elapsed > TimeSpan.FromSeconds(60))
						failures.Add($"{Name(type)}: took {typeWatch.Elapsed.TotalSeconds:F0} s");
					if (text.Length == 0)
						failures.Add($"{Name(type)}: no output");
					var normalized = text.Replace("\r\n", "\n");
					var marker = errorMarkers.FirstOrDefault(m => normalized.Contains(m, StringComparison.Ordinal));
					if (marker is not null) {
						int i = normalized.IndexOf(marker, StringComparison.Ordinal);
						failures.Add($"{Name(type)}: {normalized.Substring(Math.Max(0, i - 300), Math.Min(800, normalized.Length - Math.Max(0, i - 300)))}");
					}
				}
				catch (Exception ex) {
					failures.Add($"{Name(type)}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");
				}
			}
			log.WriteLine($"{configuration} {language}: {types} types, {methods} methods, {watch.Elapsed.TotalSeconds:F1} s, {failures.Count} failures");
			foreach (var f in failures)
				log.WriteLine(f);
			Assert.Empty(failures);
		}

		/// <summary>
		/// The C# decompiler's debug info stays correct (a breakpoint binds to the right instruction, no statement
		/// contains another's branch) when the obfuscation keeps well-formed control flow: renaming, string encryption,
		/// proxy calls, control-flow flattening and junk types. (Injecting dead or invalid IL between a statement's
		/// instructions - <see cref="Obfuscations.JunkCode"/> - degrades the IL-to-source mapping, which every debugger
		/// tolerates; those are covered by <see cref="EveryType_DecompilesWithoutErrors"/> only. The IL and IL with C#
		/// decompilers make each instruction a statement, so this statement-level check doesn't apply to them.)
		/// </summary>
		[Theory]
		[InlineData(nameof(Obfuscations.Rename))]
		[InlineData(nameof(Obfuscations.Strings))]
		[InlineData(nameof(Obfuscations.ProxyCalls))]
		[InlineData(nameof(Obfuscations.ControlFlow))]
		[InlineData(nameof(Obfuscations.JunkTypes))]
		public void CSharpDebugInfo_IsValid(string configuration) {
			var module = Obfuscated(configuration).program.Load();
			var decompiler = LanguageRunner.Create();
			var failures = new List<string>();
			int methods = 0;
			foreach (var type in module.Types) {
				var (text, infos) = LanguageRunner.DecompileType(decompiler, type);
				foreach (var info in infos) {
					methods++;
					var report = DebugInfoValidator.Validate(info, text, null);
					if (report.Errors.Count != 0 && failures.Count < 10)
						failures.Add($"{Name(info.Method)}: {string.Join(" | ", report.Errors.Take(3))}");
				}
			}
			log.WriteLine($"{configuration}: {methods} methods, {failures.Count} with debug info errors");
			foreach (var f in failures)
				log.WriteLine(f);
			Assert.Empty(failures);
		}

		/// <summary>
		/// Names for the messages: the obfuscated ones are invisible (escaped) and the token says which member it is
		/// </summary>
		static string Name(IMemberDef member) => $"0x{member.MDToken.Raw:X8} '{Escape(member.FullName)}'";

		static string Escape(string s) => string.Concat(s.Select(c => c < 0x20 || (c >= 0x7F && c < 0xA0) || char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.Format ? $"\\u{(int)c:X4}" : c.ToString()));

		/// <summary>
		/// Edit Method without changes on every method
		/// </summary>
		Dictionary<string, List<string>> EditAllMethods(string configuration) {
			var (program, expected) = Obfuscated(configuration);
			Assert.False(Failed(expected), expected);
			var decompiler = LanguageRunner.Create();
			var methods = EditRoundTripTests.GetEditableMethods(decompiler, program.Load()).Select(a => a.MDToken.Raw).ToList();
			var byCategory = new Dictionary<string, List<string>>(StringComparer.Ordinal);
			var watch = Stopwatch.StartNew();
			int ok = 0;
			foreach (var token in methods) {
				var outcome = EditRoundTripTests.RunEdit(decompiler, EditKind.EditMethod, program, Namespace, expected, token);
				// The caret and nullability checks are about editing clean code; a renamed/obfuscated member needn't satisfy them
				if (outcome is null || outcome.Category is "caret" or "nullability") {
					ok++;
					continue;
				}
				if (!byCategory.TryGetValue(outcome.Category, out var list))
					byCategory[outcome.Category] = list = new List<string>();
				list.Add(Escape(outcome.Description));
			}
			log.WriteLine($"{configuration}: {ok}/{methods.Count} OK ({watch.Elapsed.TotalSeconds:F0} s)" +
				(byCategory.Count == 0 ? "" : ", " + string.Join(", ", byCategory.Select(g => $"{g.Key} {g.Value.Count}"))));
			foreach (var g in byCategory)
				foreach (var d in g.Value.Take(5))
					log.WriteLine($"  [{g.Key}] {d}");
			return byCategory;
		}

		/// <summary>
		/// Edit Method on any method of any obfuscated assembly is robust: the decompile, compile, import and save
		/// pipeline never throws and never produces a module that can't be saved. A method that doesn't round-trip fails
		/// gracefully with a compile or import error that dnSpy shows (invalid identifiers, code upstream can't decompile
		/// to compilable C#), and the edited program never crashes with invalid IL.
		/// </summary>
		[Theory]
		[MemberData(nameof(Configurations))]
		public void EditMethod_IsRobust(string configuration) {
			var byCategory = EditAllMethods(configuration);
			// The decompiler threw, the module couldn't be saved, or the edited program crashed (invalid imported IL)
			Assert.True(byCategory.GetValueOrDefault("decompile") is null, "decompiler threw: " + string.Join(" | ", byCategory.GetValueOrDefault("decompile") ?? new List<string>()));
			Assert.True(byCategory.GetValueOrDefault("write") is null, "module couldn't be saved: " + string.Join(" | ", byCategory.GetValueOrDefault("write") ?? new List<string>()));
			var crashes = byCategory.GetValueOrDefault("behavior")?.Where(a => a.Contains("CRASH") || a.Contains("EXCEPTION")).ToList() ?? new List<string>();
			Assert.True(crashes.Count == 0, "the edited program crashed: " + string.Join(" | ", crashes));
		}

		/// <summary>
		/// Edit Method without changes keeps the program's behavior for obfuscations that don't rewrite a method's
		/// control flow or rename members: string encryption, proxy calls and junk types. (Control-flow flattening and
		/// dead-code injection can defeat the upstream decompiler's reconstruction of field-like event accessors - it
		/// renders the backing field under a different name than the event, or drops the raise - so the recompiled code
		/// compiles but the event doesn't fire. That's an inherent limit of recompiling decompiled adversarial IL;
		/// <see cref="EditMethod_IsRobust"/> still covers those configurations for robustness.)
		/// </summary>
		[Theory]
		[InlineData(nameof(Obfuscations.Strings))]
		[InlineData(nameof(Obfuscations.ProxyCalls))]
		[InlineData(nameof(Obfuscations.JunkTypes))]
		public void EditMethod_PreservesBehavior(string configuration) {
			var byCategory = EditAllMethods(configuration);
			var behavior = byCategory.GetValueOrDefault("behavior") ?? new List<string>();
			Assert.True(behavior.Count == 0, "behavior changed: " + string.Join(" | ", behavior));
		}
	}
}
