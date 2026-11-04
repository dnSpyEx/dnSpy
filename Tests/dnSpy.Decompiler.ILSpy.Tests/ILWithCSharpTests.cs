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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.Mixed;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// "IL with C#": dnSpy's IL with the C# decompiler's statements above their IL instructions
	/// </summary>
	public class ILWithCSharpTests {
		readonly ITestOutputHelper log;
		public ILWithCSharpTests(ITestOutputHelper log) => this.log = log;

		static IDecompiler Create(out IDecompiler csharp) {
			csharp = LanguageRunner.Create();
			return ILWithCSharpDecompiler.Create(DecompilerSettingsService.__Instance_DONT_USE.ILDecompilerSettings, csharp);
		}

		[Fact]
		public void Identity() {
			var decompiler = Create(out var csharp);
			// The names and GUIDs of the languages it replaced (saved tabs and settings keep them)
			Assert.Equal("C#", csharp.UniqueNameUI);
			Assert.Equal(DecompilerConstants.LANGUAGE_CSHARP_ILSPY, csharp.UniqueGuid);
			Assert.Equal(DecompilerConstants.LANGUAGE_CSHARP, csharp.GenericGuid);
			Assert.Equal(DecompilerConstants.CSHARP_ILSPY_ORDERUI, csharp.OrderUI);
			Assert.Equal("IL with C#", decompiler.UniqueNameUI);
			Assert.Equal(DecompilerConstants.LANGUAGE_IL, decompiler.GenericGuid);
			Assert.Equal(DecompilerConstants.LANGUAGE_IL_WITH_CSHARP_ILSPY, decompiler.UniqueGuid);
			Assert.Equal(DecompilerConstants.IL_WITH_CSHARP_ILSPY_ORDERUI, decompiler.OrderUI);
			Assert.Equal(".il", decompiler.FileExtension);
		}

		/// <summary>
		/// The C# lines in the IL view ('// ' comments) of a method
		/// </summary>
		static List<string> GetCSharpLines(string il) =>
			il.Split('\n').Select(a => a.TrimEnd('\r').Trim()).Where(a => a.StartsWith("// ", StringComparison.Ordinal) && !a.StartsWith("// ^", StringComparison.Ordinal))
			.Select(a => a.Substring(3).Trim()).Where(a => a.Length != 0).ToList();

		/// <summary>
		/// Every statement of the C# view is shown in the IL view, above an IL instruction
		/// </summary>
		[Theory]
		[InlineData("ControlFlow")]
		[InlineData("Statements")]
		public void Methods_ShowTheirCSharpStatements(string program) {
			var source = program == "ControlFlow" ? TestPrograms.ControlFlow : TestPrograms.Statements;
			var module = TestCompiler.Compile(program, source).Load();
			var decompiler = Create(out var csharp);
			int methods = 0, statements = 0;
			var missing = new List<string>();
			foreach (var method in module.GetTypes().SelectMany(a => a.Methods).Where(a => a.HasBody && !a.IsConstructor && !IsCompilerGenerated(a))) {
				var code = LanguageRunner.Decompile(csharp, method);
				var info = code.DebugInfos.FirstOrDefault(a => a.Method == method);
				if (info is null)
					continue;
				var il = LanguageRunner.Decompile(decompiler, method).Text;
				Assert.Contains("IL_0000:", il);
				var lines = new HashSet<string>(GetCSharpLines(il), StringComparer.Ordinal);
				methods++;
				foreach (var statement in info.Statements) {
					var span = statement.TextSpan;
					if (span.Length == 0)
						continue;
					// The line of the statement in the C# view
					var text = code.Text;
					int start = text.LastIndexOf('\n', Math.Max(0, span.Start - 1)) + 1;
					int end = text.IndexOf('\n', span.Start);
					var line = text.Substring(start, (end < 0 ? text.Length : end) - start).Trim();
					statements++;
					if (!lines.Contains(line))
						missing.Add($"{method.FullName}: {line}");
				}
			}
			log.WriteLine($"{program}: {methods} methods, {statements} statements");
			Assert.True(methods >= 5, methods.ToString());
			Assert.Empty(missing.Take(10));
		}

		static bool IsCompilerGenerated(MethodDef method) =>
			method.Name.String.Contains('<') || method.DeclaringType.Name.String.Contains('<');

		/// <summary>
		/// The MoveNext method of an async method or iterator shows the statements of the async method / iterator
		/// </summary>
		[Theory]
		[InlineData("<Compute>", "await Task.FromResult(x)")]
		[InlineData("<Compute>", "return a;")]
		[InlineData("<Numbers>", "yield return i;")]
		[InlineData("<Stream>", "await Task.Yield();")]
		public void StateMachineMoveNext_ShowsTheStatementsOfTheMethod(string typeNamePrefix, string statement) {
			var module = TestCompiler.Compile("StateMachines", TestPrograms.StateMachines).Load();
			var type = module.GetTypes().First(a => a.Name.StartsWith(typeNamePrefix, StringComparison.Ordinal));
			var moveNext = type.Methods.First(a => a.Name == "MoveNext");
			var il = LanguageRunner.Decompile(Create(out _), moveNext).Text;
			var lines = GetCSharpLines(il);
			Assert.Contains(lines, a => a.Contains(statement, StringComparison.Ordinal));
		}

		/// <summary>
		/// The IL view of a type shows the C# statements of its methods
		/// </summary>
		[Fact]
		public void Type_ShowsTheStatementsOfItsMethods() {
			var module = TestCompiler.Compile("StateMachines", TestPrograms.StateMachines).Load();
			var type = module.GetTypes().First(a => a.Name == "StateMachines");
			var il = LanguageRunner.Decompile(Create(out _), type).Text;
			Assert.Contains(".class public auto ansi beforefieldinit StateMachines", il);
			var lines = GetCSharpLines(il);
			Assert.Contains(lines, a => a.Contains("return v * 2;", StringComparison.Ordinal) || a.Contains("Task.Yield()", StringComparison.Ordinal));
			Assert.Contains(lines, a => a.Contains("levels--", StringComparison.Ordinal));
		}
	}
}
