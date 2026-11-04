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

#if DEBUG
using System;
using System.Collections.Generic;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.ILAst;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.DebugSteps;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The upstream decompiler's ILAst views (debug builds): typed IL, the ILAst as read and after the IL transforms,
	/// and the stepper
	/// </summary>
	public class ILAstTests {
		readonly ITestOutputHelper log;
		public ILAstTests(ITestOutputHelper log) => this.log = log;

		static UpstreamILAstLanguage Create(UpstreamILAstView view, UpstreamCSharpLanguage? csharp = null) =>
			UpstreamILAstLanguage.Create(csharp ?? LanguageRunner.Create()).Single(a => a.View == view);

		static ModuleDef LoadControlFlow() => TestCompiler.Compile("ILAst_ControlFlow", TestPrograms.ControlFlow).Load();

		[Fact]
		public void Identity() {
			var languages = new UpstreamDecompilerProvider().Create().OfType<UpstreamILAstLanguage>().ToList();
			Assert.Equal(new[] { UpstreamILAstView.TypedIL, UpstreamILAstView.Unoptimized, UpstreamILAstView.Transformed }, languages.Select(a => a.View));
			Assert.Equal(new[] { "Typed IL", "ILAst (unoptimized)", "ILAst" }, languages.Select(a => a.UniqueNameUI));
			Assert.All(languages, a => Assert.Equal(DecompilerConstants.LANGUAGE_ILAST_ILSPY, a.GenericGuid));
			Assert.Equal(3, languages.Select(a => a.UniqueGuid).Distinct().Count());
			Assert.Equal(languages.OrderBy(a => a.OrderUI), languages);
			Assert.True(languages[0].OrderUI >= DecompilerConstants.ILAST_ILSPY_DEBUG_ORDERUI);
			// dnSpy.Console's "IL with C#" uses the first C# language of the provider
			Assert.IsType<UpstreamCSharpLanguage>(new UpstreamDecompilerProvider().Create().First(a => a.GenericGuid == DecompilerConstants.LANGUAGE_CSHARP));
		}

		[Fact]
		public void Views_ShowTheMethod() {
			var module = LoadControlFlow();
			var method = module.Find("ControlFlow", false).FindMethod("Exceptions");
			var csharp = LanguageRunner.Create();

			var typedIL = LanguageRunner.Decompile(Create(UpstreamILAstView.TypedIL, csharp), method);
			log.WriteLine(typedIL.Text);
			Assert.StartsWith("// System.String ControlFlow::Exceptions(System.String)", typedIL.Text, StringComparison.Ordinal);
			Assert.Contains("Block IL_0000 {", typedIL.Text);
			Assert.Contains("callvirt ReadToEnd(ldloc V_0)", typedIL.Text);
			Assert.Contains("[S_0:obj]", typedIL.Text);                    // the inferred stack
			Assert.Contains(".try IL_0001-IL_001d filter", typedIL.Text);  // the exception handlers

			var unoptimized = LanguageRunner.Decompile(Create(UpstreamILAstView.Unoptimized, csharp), method).Text;
			log.WriteLine(unoptimized);
			Assert.Contains("ILFunction Exceptions", unoptimized);
			Assert.Contains("BlockContainer", unoptimized);
			Assert.Contains("stloc", unoptimized);

			var transformed = LanguageRunner.Decompile(Create(UpstreamILAstView.Transformed, csharp), method);
			log.WriteLine(transformed.Text);
			Assert.Contains("ILFunction Exceptions", transformed.Text);
			Assert.Contains("using", transformed.Text);           // UsingTransform
			Assert.Contains("try", transformed.Text);
			Assert.DoesNotContain("A transform failed", transformed.Text);
			Assert.NotEqual(unoptimized, transformed.Text);

			// References are dnSpy's: the method, its module's types and imported members
			Assert.Contains(transformed.References, a => a.Reference == method && (a.Flags & DecompilerReferenceFlags.Definition) != 0);
			Assert.Contains(transformed.References, a => a.Reference is IMethod m && m.Name == "WriteLine");
			Assert.Contains(transformed.References, a => (a.Flags & DecompilerReferenceFlags.Local) != 0 && a.Text == "stringReader");
			Assert.Contains(typedIL.References, a => a.Reference is IMethod m && m.Name == "ReadToEnd");
		}

		[Fact]
		public void Stepper_RecordsOneStepPerTransform_AndStopsAtTheLimit() {
			var module = LoadControlFlow();
			var type = module.Find("ControlFlow", false);
			var loops = type.FindMethod("Loops");
			var language = Create(UpstreamILAstView.Transformed);
			int updates = 0;
			language.StepperUpdated += (s, e) => updates++;
			var disabledCaching = new List<IDecompilerOutput>();
			language.DisableOutputCaching = disabledCaching.Add;

			var full = LanguageRunner.Decompile(language, loops);
			Assert.Equal(1, updates);
			Assert.Equal(new[] { full }, disabledCaching);
			Assert.Same(loops, language.StepperMethod);
			var steps = language.Stepper.Steps;
			foreach (var step in steps)
				log.WriteLine($"{step.Description} [{step.BeginStep}, {step.EndStep})");
			if (!Stepper.SteppingAvailable)
				Assert.Equal(CSharpDecompiler.GetILTransforms().Count, steps.Count);
			Assert.StartsWith("0: ", steps[0].Description, StringComparison.Ordinal);
			Assert.Contains(steps, a => a.Description.Contains("BlockILTransform", StringComparison.Ordinal));

			// The state after the first block transform (control flow): the stepper keeps its steps
			int index = steps.ToList().FindIndex(a => a.Description.Contains("BlockILTransform", StringComparison.Ordinal));
			var stepper = language.Stepper;
			language.SetStepLimit(steps[index].EndStep);
			var limited = LanguageRunner.Decompile(language, loops).Text;
			log.WriteLine(limited);
			Assert.Contains($"// Before step {steps[index].EndStep}: ", limited);
			Assert.NotEqual(full.Text, limited);
			Assert.Same(stepper, language.Stepper);
			Assert.Equal(1, updates);

			// The limit is the method's: a type and the other methods show all steps, and decompiling another method
			// shows its steps and forgets the limit
			var typeText = LanguageRunner.Decompile(language, type).Text;
			Assert.DoesNotContain("// Before step", typeText);
			Assert.Equal(1, updates);
			Assert.Single(disabledCaching.Skip(1));
			var other = type.FindMethod("SwitchStatement");
			Assert.DoesNotContain("// Before step", LanguageRunner.Decompile(language, other).Text);
			Assert.Equal(2, updates);
			Assert.Same(other, language.StepperMethod);
			Assert.Equal(full.Text, LanguageRunner.Decompile(language, loops).Text);

			// The last step shows all of them
			language.SetStepLimit(language.Stepper.Steps.Last().EndStep);
			Assert.Equal(full.Text, LanguageRunner.Decompile(language, loops).Text);
		}

		/// <summary>
		/// Every method of the test programs in every view: no failed transforms
		/// </summary>
		[Fact]
		public void TestPrograms_AllViews() {
			var csharp = LanguageRunner.Create();
			var languages = UpstreamILAstLanguage.Create(csharp).ToArray();
			int methods = 0;
			foreach (var (name, source) in new[] { ("ControlFlow", TestPrograms.ControlFlow), ("Statements", TestPrograms.Statements), ("StateMachines", TestPrograms.StateMachines) }) {
				var module = TestCompiler.Compile("ILAst_" + name, source).Load();
				foreach (var type in module.GetTypes()) {
					foreach (var language in languages) {
						var text = LanguageRunner.Decompile(language, type).Text;
						Assert.DoesNotContain("A transform failed", text);
						Assert.DoesNotContain("can't be decompiled", text);
					}
					methods += type.Methods.Count(a => a.HasBody);
				}
			}
			log.WriteLine($"{methods} methods");
			Assert.True(methods > 50, methods.ToString());
		}
	}
}
#endif
