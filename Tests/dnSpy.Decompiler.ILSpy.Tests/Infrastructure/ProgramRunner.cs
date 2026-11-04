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
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// Runs a test program's Program.Run() in its own process: edited code can be wrong in ways that crash the
	/// process (e.g. invalid IL or memory access in unsafe code)
	/// </summary>
	static class ProgramRunner {
		const string RunnerSource = """
			using System;
			using System.IO;
			using System.Runtime.Loader;
			public static class Runner {
				public static int Main(string[] args) {
					Console.OutputEncoding = System.Text.Encoding.UTF8;
					try {
						var asm = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.GetFullPath(args[0]));
						var result = (string)asm.GetType(args[1] + ".Program")!.GetMethod("Run")!.Invoke(null, null)!;
						Console.Out.Write(result);
						return 0;
					}
					catch (Exception ex) {
						Console.Out.Write("EXCEPTION " + (ex.InnerException ?? ex).ToString());
						return 1;
					}
				}
			}
			""";

		static readonly Lazy<string> directory = new Lazy<string>(() => {
			var dir = Path.Combine(Path.GetTempPath(), "dnspy-edit-runner-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(dir);
			var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
			var references = tpa.Split(Path.PathSeparator).Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				.Where(a => !Path.GetFileName(a).StartsWith("dnSpy", StringComparison.OrdinalIgnoreCase))
				.Select(a => (MetadataReference)MetadataReference.CreateFromFile(a));
			var compilation = CSharpCompilation.Create("Runner", new[] { CSharpSyntaxTree.ParseText(RunnerSource) }, references,
				new CSharpCompilationOptions(OutputKind.ConsoleApplication, nullableContextOptions: NullableContextOptions.Enable));
			using (var pe = File.Create(Path.Combine(dir, "Runner.dll"))) {
				var result = compilation.Emit(pe);
				if (!result.Success)
					throw new InvalidOperationException(string.Join(Environment.NewLine, result.Diagnostics));
			}
			var version = Environment.Version;
			File.WriteAllText(Path.Combine(dir, "Runner.runtimeconfig.json"),
				"{\"runtimeOptions\":{\"tfm\":\"net" + version.Major + ".0\",\"framework\":{\"name\":\"Microsoft.NETCore.App\",\"version\":\"" + version.Major + ".0.0\"}}}");
			return dir;
		});

		/// <summary>
		/// Program.Run()'s result, or "EXCEPTION ..." / "CRASH ..." / "TIMEOUT"
		/// </summary>
		public static string Run(byte[] image, string ns) {
			var dir = directory.Value;
			var file = Path.Combine(dir, Guid.NewGuid().ToString("N") + ".dll");
			File.WriteAllBytes(file, image);
			try {
				var dotnet = Environment.ProcessPath is string path && Path.GetFileNameWithoutExtension(path) == "dotnet" ? path : "dotnet";
				var info = new ProcessStartInfo(dotnet) {
					RedirectStandardOutput = true,
					RedirectStandardError = true,
					UseShellExecute = false,
					StandardOutputEncoding = Encoding.UTF8,
				};
				info.ArgumentList.Add("exec");
				info.ArgumentList.Add(Path.Combine(dir, "Runner.dll"));
				info.ArgumentList.Add(file);
				info.ArgumentList.Add(ns);
				using var process = Process.Start(info)!;
				var stdout = process.StandardOutput.ReadToEndAsync();
				var stderr = process.StandardError.ReadToEndAsync();
				if (!process.WaitForExit(60_000)) {
					process.Kill(entireProcessTree: true);
					return "TIMEOUT";
				}
				if (process.ExitCode != 0 && !stdout.Result.StartsWith("EXCEPTION", StringComparison.Ordinal))
					return $"CRASH exit code {process.ExitCode}: {stdout.Result} {stderr.Result}";
				return stdout.Result;
			}
			finally {
				try {
					File.Delete(file);
				}
				catch {
				}
			}
		}
	}
}
