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
using System.Xml.Linq;
using dnlib.DotNet;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The .NET Framework build: every reference to an assembly that dnSpy ships resolves to the shipped version, by
	/// itself or with a binding redirect in the exe's config file. Without one, .NET Framework can't load the assembly
	/// (Mono ignores the version) and the code that uses it fails, e.g. the ILSpy package references
	/// System.Runtime.CompilerServices.Unsafe 6.0.0.0 and dnSpy ships 6.0.3.0. (dnSpy.exe's config gets the redirects
	/// the build finds, dnSpy.Console.exe loads the decompiler at runtime so the build can't find them and they're in
	/// app.config.) And every binding redirect is to the version dnSpy ships (a Roslyn update needs new redirects for
	/// extensions built against an older Roslyn). Checks the net48 build output when it's there (CI builds it), so it
	/// catches package updates that need app.config updates.
	/// </summary>
	public class BindingRedirectTests {
		readonly ITestOutputHelper log;

		public BindingRedirectTests(ITestOutputHelper log) => this.log = log;

		static string? FindRepositoryRoot() {
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
				if (File.Exists(Path.Combine(dir.FullName, "dnSpy.sln")))
					return dir.FullName;
			}
			return null;
		}

		/// <summary>
		/// The exes and the assemblies they load at runtime (dnSpy loads all extensions, *.x.dll)
		/// </summary>
		static readonly (string exe, string[] loaded)[] processes = {
			("dnSpy.exe", new[] { "*.x.dll" }),
			("dnSpy-x86.exe", new[] { "*.x.dll" }),
			("dnSpy.Console.exe", new[] { "dnSpy.Decompiler.ILSpy.Core.dll", "dnSpy.BamlDecompiler.x.dll" }),
		};

		[Fact]
		public void NetFramework_ReferencesResolveToShippedVersions() {
			var root = FindRepositoryRoot();
			Assert.NotNull(root);
			var binDir = Path.Combine(root!, "dnSpy", "dnSpy", "bin");
			var outputDirs = new[] { "Debug", "Release" }.Select(a => Path.Combine(binDir, a, "net48")).Where(Directory.Exists).ToList();
			var problems = new List<string>();
			var assembliesByDir = new Dictionary<string, List<AssemblyInfo>>(StringComparer.Ordinal);
			int checkedExes = 0;
			foreach (var dir in outputDirs) {
				foreach (var (exe, loaded) in processes) {
					var exePath = Path.Combine(dir, exe);
					if (!File.Exists(exePath) || !File.Exists(exePath + ".config"))
						continue;
					checkedExes++;
					var config = ReadConfig(exePath + ".config");
					var redirects = config.redirects;
					var where = $"{exe} ({Path.GetFileName(Path.GetDirectoryName(dir))})";
					// The assemblies in the exe's directory and its probing paths (build.ps1 moves them to 'bin'), not the
					// debugger's processes' in the other subdirectories
					var dirs = new[] { dir }.Concat(config.probingPaths.Select(a => Path.Combine(dir, a))).Where(Directory.Exists).ToList();
					var assemblies = new Dictionary<string, AssemblyInfo>(StringComparer.OrdinalIgnoreCase);
					foreach (var d in dirs) {
						if (!assembliesByDir.TryGetValue(d, out var list))
							assembliesByDir.Add(d, list = ReadAssemblies(d));
						foreach (var a in list)
							assemblies.TryAdd(a.Name, a);
					}
					var roots = new[] { exe }.Concat(dirs.SelectMany(d => loaded.SelectMany(a => Directory.GetFiles(d, a))).Select(Path.GetFileName));
					var closure = GetClosure(assemblies, roots.Select(a => assemblies.Values.FirstOrDefault(b => StringComparer.OrdinalIgnoreCase.Equals(Path.GetFileName(b.File), a))));
					log.WriteLine($"{exePath}: {closure.Count} assemblies, {redirects.Count} binding redirects");
					foreach (var asm in closure) {
						foreach (var reference in asm.References) {
							if (!assemblies.TryGetValue(reference.Name, out var target) || target.Version == reference.Version)
								continue;
							if (redirects.TryGetValue(reference.Name, out var redirect) && redirect.OldMin <= reference.Version &&
								reference.Version <= redirect.OldMax && redirect.NewVersion == target.Version)
								continue;
							problems.Add($"{where}: {Path.GetFileName(asm.File)} references {reference.Name} {reference.Version}, the {Path.GetFileName(target.File)} dnSpy ships is {target.Version}" +
								(redirect is null ? ", no binding redirect" : $", binding redirect {redirect.OldMin}-{redirect.OldMax} -> {redirect.NewVersion}"));
						}
					}
					// A redirect to another version than the one dnSpy ships, e.g. after a Roslyn update: dnSpy's own
					// assemblies reference the new version, but extensions and scripts built against an older one can't
					// load it
					foreach (var (name, redirect) in redirects) {
						if (assemblies.TryGetValue(name, out var target) && redirect.NewVersion != target.Version)
							problems.Add($"{where}: binding redirect {name} {redirect.OldMin}-{redirect.OldMax} -> {redirect.NewVersion}, the {Path.GetFileName(target.File)} dnSpy ships is {target.Version}");
					}
				}
			}
			if (checkedExes == 0)
				log.WriteLine("No net48 build output in " + binDir);
			Assert.True(problems.Count == 0, "Update the binding redirects in dnSpy/dnSpy/app.config:" + Environment.NewLine + string.Join(Environment.NewLine, problems.Distinct()));
		}

		static List<AssemblyInfo> GetClosure(Dictionary<string, AssemblyInfo> assemblies, IEnumerable<AssemblyInfo?> roots) {
			var result = new List<AssemblyInfo>();
			var seen = new HashSet<AssemblyInfo>();
			var stack = new Stack<AssemblyInfo>(roots.Where(a => a is not null)!);
			while (stack.Count > 0) {
				var asm = stack.Pop();
				if (!seen.Add(asm))
					continue;
				result.Add(asm);
				foreach (var reference in asm.References) {
					if (assemblies.TryGetValue(reference.Name, out var target))
						stack.Push(target);
				}
			}
			return result;
		}

		sealed record Redirect(Version OldMin, Version OldMax, Version NewVersion);
		sealed record AssemblyReference(string Name, Version Version);
		sealed record AssemblyInfo(string File, string Name, Version Version, List<AssemblyReference> References);

		static (Dictionary<string, Redirect> redirects, List<string> probingPaths) ReadConfig(string configFile) {
			var result = new Dictionary<string, Redirect>(StringComparer.OrdinalIgnoreCase);
			var doc = XDocument.Load(configFile);
			foreach (var dependentAssembly in doc.Descendants().Where(a => a.Name.LocalName == "dependentAssembly")) {
				var name = dependentAssembly.Elements().FirstOrDefault(a => a.Name.LocalName == "assemblyIdentity")?.Attribute("name")?.Value;
				var redirect = dependentAssembly.Elements().FirstOrDefault(a => a.Name.LocalName == "bindingRedirect");
				var oldVersion = redirect?.Attribute("oldVersion")?.Value.Split('-');
				var newVersion = redirect?.Attribute("newVersion")?.Value;
				if (name is null || oldVersion is null || newVersion is null)
					continue;
				result[name] = new Redirect(Version.Parse(oldVersion[0]), Version.Parse(oldVersion[oldVersion.Length - 1]), Version.Parse(newVersion));
			}
			// <probing privatePath="bin"/>
			var probingPaths = doc.Descendants().Where(a => a.Name.LocalName == "probing")
				.SelectMany(a => (a.Attribute("privatePath")?.Value ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
			return (result, probingPaths);
		}

		static List<AssemblyInfo> ReadAssemblies(string dir) {
			var result = new List<AssemblyInfo>();
			foreach (var file in Directory.EnumerateFiles(dir)) {
				if (!file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
					continue;
				try {
					using var module = ModuleDefMD.Load(File.ReadAllBytes(file));
					if (module.Assembly is null)
						continue;
					var references = module.GetAssemblyRefs().Select(a => new AssemblyReference(a.Name, a.Version)).ToList();
					result.Add(new AssemblyInfo(file, module.Assembly.Name, module.Assembly.Version, references));
				}
				catch (BadImageFormatException) {
					// Native DLLs
				}
			}
			return result;
		}
	}
}
