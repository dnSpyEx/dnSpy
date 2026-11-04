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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.CorpusRunner {
	/// <summary>
	/// inspect --type FULLNAME [--method NAME] [--text true] ASSEMBLY
	/// Prints a method's IL and the debug info statements the decompiler produced for it (--text: and its decompiled
	/// code). With the environment variable SHAPER_TRACE set to part of a method's full name, also prints the
	/// statement shaper's steps for that method.
	/// </summary>
	static class Inspect {
		public static int Run(Options options) {
			dnSpy.Decompiler.ILSpy.Core.DebugInfo.StatementShaper.OnError = (method, ex) => Console.WriteLine($"SHAPER ERROR {method.FullName}: {ex}");
			if (Environment.GetEnvironmentVariable("SHAPER_TRACE") is string traceFilter) {
				bool tracing = false;
				dnSpy.Decompiler.ILSpy.Core.DebugInfo.StatementShaper.Trace.Value = line => {
					if (line.StartsWith("==", StringComparison.Ordinal))
						tracing = line.Contains(traceFilter, StringComparison.Ordinal);
					if (tracing)
						Console.WriteLine(line);
				};
			}
			var module = ModuleDefMD.Load(options.Paths.Single(), AssemblyRunner.CreateModuleContext(options.Paths.Single()));
			var typeName = options.Get("type");
			var methodName = options.Get("method", "");
			var type = module.GetTypes().First(t => t.FullName == typeName);
			var top = type;
			while (top.DeclaringType is not null)
				top = top.DeclaringType;
			var d = new UpstreamCSharpDecompiler(new ModuleBridgeCache(new ModuleBridgeOptions()), new DecompilerSettings(LanguageVersion.Latest) { ThrowOnAssemblyResolveErrors = false });
			var r = d.DecompileType(top);
			var (text, infos) = (r.Text, r.MethodDebugInfos);
			foreach (var kv in r.RawSequencePoints!) {
				if (kv.Key.MoveNextMethod?.Name != methodName && kv.Key.Method?.Name != methodName)
					continue;
				if (kv.Key.MoveNextMethod is { } mn && mn.DeclaringTypeDefinition?.Name != type.Name.String)
					continue;
				Console.WriteLine($"== raw upstream sequence points for {kv.Key.Method?.FullName} (MoveNext: {kv.Key.MoveNextMethod?.FullName})");
				foreach (var sp in kv.Value)
					Console.WriteLine($"   IL_{sp.Offset:X4}-IL_{sp.EndOffset:X4} {sp.StartLine}:{sp.StartColumn}-{sp.EndLine}:{sp.EndColumn}{(sp.IsHidden ? " hidden" : "")}");
			}
			foreach (var info in infos.Where(i => i.Method.DeclaringType == type && (methodName.Length == 0 || i.Method.Name == methodName))) {
				Console.WriteLine($"== {info.Method.FullName} span={info.Span}");
				foreach (var instr in info.Method.Body.Instructions)
					Console.WriteLine($"   {instr}");
				var report = DebugInfoValidator.Validate(info, text, null);
				foreach (var st in info.Statements.OrderBy(s => s.ILSpan.Start)) {
					var t = text.Substring(st.TextSpan.Start, Math.Min(st.TextSpan.Length, 80)).Replace("\n", "\\n");
					Console.WriteLine($"   {st.ILSpan} {st.TextSpan} `{t}`");
				}
				foreach (var e in report.Errors)
					Console.WriteLine("   ERROR " + e);
				if (options.Get("text", "") == "true")
					Console.WriteLine(text.Substring(info.Span.Start, info.Span.Length));
			}
			return 0;
		}
	}
}
