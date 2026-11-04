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
using System.Text;

namespace dnSpy.UITests.Infrastructure {
	/// <summary>
	/// The programs the tests open in dnSpy, built once per test run into the run's output directory
	/// (%TEMP%\dnSpy.UITests\&lt;run&gt;, or DNSPY_UITEST_OUT)
	/// </summary>
	static class Samples {
		static readonly Dictionary<string, string> built = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

		public static string RepoRoot { get; } = FindRepoRoot();

		public static string OutputRoot { get; } = CreateOutputRoot();

		static string FindRepoRoot() {
			for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
				if (File.Exists(Path.Combine(dir.FullName, "dnSpy.sln")))
					return dir.FullName;
			}
			throw new InvalidOperationException("Couldn't find dnSpy.sln above " + AppContext.BaseDirectory);
		}

		static string CreateOutputRoot() {
			var root = Environment.GetEnvironmentVariable("DNSPY_UITEST_OUT");
			if (string.IsNullOrEmpty(root))
				root = Path.Combine(Path.GetTempPath(), "dnSpy.UITests", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
			Directory.CreateDirectory(root);
			return root;
		}

		public static string NewWorkDirectory(string name) {
			var dir = Path.Combine(OutputRoot, "work", name + "-" + Guid.NewGuid().ToString("N").Substring(0, 8));
			Directory.CreateDirectory(dir);
			return dir;
		}

		/// <summary>
		/// Builds a sample project (path relative to the repo) and returns its main output file
		/// </summary>
		public static string Build(string project, string configuration, string outputFile) {
			var key = project + "|" + configuration;
			lock (built) {
				if (built.TryGetValue(key, out var file))
					return file;
				var outDir = Path.Combine(OutputRoot, "samples", Path.GetFileNameWithoutExtension(project) + "-" + configuration);
				var (exitCode, output) = Run("dotnet", Samples.RepoRoot, "build", Path.Combine(RepoRoot, project), "-c", configuration, "-o", outDir, "-nologo", "-v", "q");
				if (exitCode != 0)
					throw new InvalidOperationException($"Couldn't build {project} ({configuration}):\n{output}");
				file = Path.Combine(outDir, outputFile);
				if (!File.Exists(file))
					throw new FileNotFoundException("The build didn't produce " + file);
				built[key] = file;
				return file;
			}
		}

		/// <summary>
		/// DebuggerPlayground.dll (no PDB: dnSpy uses the decompiler's debug info)
		/// </summary>
		public static string DebuggerPlayground(string configuration) =>
			Build(Path.Combine("Tests", "DebuggerPlayground", "DebuggerPlayground.csproj"), configuration, "DebuggerPlayground.dll");

		public static string ConsoleSample(string configuration = "Release") =>
			Build(Path.Combine("Tests", "WindowsChecks", "Samples", "ConsoleSample", "ConsoleSample.csproj"), configuration, "ConsoleSample.dll");

		public static string NetFxSample(string configuration = "Release") =>
			Build(Path.Combine("Tests", "WindowsChecks", "Samples", "NetFxSample", "NetFxSample.csproj"), configuration, "NetFxSample.exe");

		/// <summary>
		/// The output of a .NET 10 app (App.dll) with a .NET 9 library (OldLib.dll) that uses a .NET 10 one (NewLib.dll)
		/// and a .NET Standard 2.0 library (StandardLib.dll) that uses System.Collections.Immutable (.NET 10's): returns
		/// the file. (Not with -o: NewLib's .NET 9 and .NET 10 builds would overwrite each other.)
		/// </summary>
		public static string MixedFrameworks(string fileName) {
			const string key = "MixedFrameworks";
			lock (built) {
				if (built.TryGetValue(key, out var file))
					return Path.Combine(Path.GetDirectoryName(file)!, fileName);
				var artifacts = Path.Combine(OutputRoot, "samples", key);
				var project = Path.Combine(RepoRoot, "Tests", "WindowsChecks", "Samples", key, "App", "App.csproj");
				var (exitCode, output) = Run("dotnet", RepoRoot, "build", project, "-c", "Release", "--artifacts-path", artifacts, "-nologo", "-v", "q");
				if (exitCode != 0)
					throw new InvalidOperationException($"Couldn't build {project}:\n{output}");
				file = Path.Combine(artifacts, "bin", "App", "release", "App.dll");
				if (!File.Exists(file))
					throw new FileNotFoundException("The build didn't produce " + file);
				built[key] = file;
				return Path.Combine(Path.GetDirectoryName(file)!, fileName);
			}
		}

		/// <summary>
		/// A copy of Newtonsoft.Json 13.0.4 (netstandard2.0, nullable annotations) the test can change and save
		/// </summary>
		public static string Newtonsoft() {
			var dir = NewWorkDirectory("newtonsoft");
			var file = Path.Combine(dir, "Newtonsoft.Json.dll");
			File.Copy(Path.Combine(AppContext.BaseDirectory, "Samples", "netstandard2.0", "Newtonsoft.Json.dll"), file);
			return file;
		}

		/// <summary>
		/// A copy of a sample's output directory, so a test can save over the module
		/// </summary>
		public static string CopyOf(string file) {
			var dir = NewWorkDirectory(Path.GetFileNameWithoutExtension(file));
			foreach (var f in Directory.GetFiles(Path.GetDirectoryName(file)!))
				File.Copy(f, Path.Combine(dir, Path.GetFileName(f)));
			return Path.Combine(dir, Path.GetFileName(file));
		}

		public static (int exitCode, string output) Run(string fileName, string workingDirectory, params string[] args) {
			var info = new ProcessStartInfo(fileName) {
				UseShellExecute = false,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				WorkingDirectory = workingDirectory,
				CreateNoWindow = true,
			};
			foreach (var a in args)
				info.ArgumentList.Add(a);
			using var process = Process.Start(info)!;
			var output = new StringBuilder();
			process.OutputDataReceived += (s, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
			process.ErrorDataReceived += (s, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			if (!process.WaitForExit(5 * 60 * 1000)) {
				process.Kill(true);
				throw new TimeoutException($"{fileName} {string.Join(" ", args)} didn't finish in 5 minutes");
			}
			process.WaitForExit();
			return (process.ExitCode, output.ToString());
		}

		/// <summary>
		/// Runs a .NET program (dll: with 'dotnet') and returns what it printed
		/// </summary>
		public static string RunProgram(string file) {
			var (exitCode, output) = file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)
				? Run("dotnet", Path.GetDirectoryName(file)!, file)
				: Run(file, Path.GetDirectoryName(file)!);
			return $"exit {exitCode}\n{output}";
		}
	}
}
