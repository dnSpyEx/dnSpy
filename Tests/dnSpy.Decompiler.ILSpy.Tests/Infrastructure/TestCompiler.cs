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
using System.Text;
using dnlib.DotNet;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// A test program compiled by Roslyn: the image plus the compiler's own portable PDB (ground truth for debug info)
	/// </summary>
	sealed class CompiledProgram {
		public string Name { get; }
		public byte[] Image { get; }
		public byte[] Pdb { get; }
		public bool Optimized { get; }

		public CompiledProgram(string name, byte[] image, byte[] pdb, bool optimized) {
			Name = name;
			Image = image;
			Pdb = pdb;
			Optimized = optimized;
		}

		/// <summary>
		/// Loads the image with dnlib, resolving references from the running runtime
		/// </summary>
		public ModuleDefMD Load() {
			var context = TestCompiler.CreateModuleContext();
			var module = ModuleDefMD.Load(Image, context);
			module.Location = Name + ".dll";
			return module;
		}
	}

	static class TestCompiler {
		static readonly Lazy<MetadataReference[]> references = new Lazy<MetadataReference[]>(() => {
			var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? string.Empty;
			return tpa.Split(Path.PathSeparator).Where(a => a.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
				.Where(a => !Path.GetFileName(a).StartsWith("dnSpy", StringComparison.OrdinalIgnoreCase))
				.Select(a => (MetadataReference)MetadataReference.CreateFromFile(a)).ToArray();
		});

		/// <summary>
		/// The framework assemblies the test programs are compiled against
		/// </summary>
		public static IEnumerable<MetadataReference> References => references.Value;

		public static CompiledProgram Compile(string name, string source, bool optimize = false, LanguageVersion languageVersion = LanguageVersion.Preview, CompiledProgram[]? references = null, OutputKind outputKind = OutputKind.DynamicallyLinkedLibrary) {
			var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), new CSharpParseOptions(languageVersion), path: name + ".cs");
			var allReferences = TestCompiler.references.Value.Concat((references ?? Array.Empty<CompiledProgram>()).Select(a => MetadataReference.CreateFromImage(a.Image)));
			var compilation = CSharpCompilation.Create(name, new[] { tree }, allReferences,
				new CSharpCompilationOptions(outputKind,
					optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug,
					allowUnsafe: true, nullableContextOptions: NullableContextOptions.Enable));
			using var pe = new MemoryStream();
			using var pdb = new MemoryStream();
			var result = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
			if (!result.Success) {
				var errors = string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
				throw new InvalidOperationException($"Test program '{name}' doesn't compile:{Environment.NewLine}{errors}");
			}
			return new CompiledProgram(name, pe.ToArray(), pdb.ToArray(), optimize);
		}

		/// <summary>
		/// Compiles a program to a directory, with its XML documentation file next to it
		/// </summary>
		public static string CompileToDisk(string name, string source, string directory) {
			Directory.CreateDirectory(directory);
			var tree = CSharpSyntaxTree.ParseText(SourceText.From(source, Encoding.UTF8), new CSharpParseOptions(LanguageVersion.Preview, DocumentationMode.Diagnose), path: name + ".cs");
			var compilation = CSharpCompilation.Create(name, new[] { tree }, references.Value,
				new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));
			var dllPath = Path.Combine(directory, name + ".dll");
			using (var pe = File.Create(dllPath))
			using (var xml = File.Create(Path.Combine(directory, name + ".xml"))) {
				var result = compilation.Emit(pe, xmlDocumentationStream: xml);
				if (!result.Success)
					throw new InvalidOperationException($"Test program '{name}' doesn't compile:{Environment.NewLine}" + string.Join(Environment.NewLine, result.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error)));
			}
			return dllPath;
		}

		public static ModuleContext CreateModuleContext() {
			var context = new ModuleContext();
			var resolver = new AssemblyResolver(context) {
				FindExactMatch = false,
				EnableFrameworkRedirect = false,
			};
			resolver.PreSearchPaths.Add(RuntimeEnvironment.GetRuntimeDirectory());
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			return context;
		}
	}
}
