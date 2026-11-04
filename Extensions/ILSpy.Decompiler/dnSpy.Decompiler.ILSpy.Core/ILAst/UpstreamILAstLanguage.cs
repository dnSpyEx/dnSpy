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
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using dnSpy.Decompiler.ILSpy.Core.Output;
using dnSpy.Decompiler.ILSpy.Core.Text;
using ICSharpCode.Decompiler.DebugSteps;
using ICSharpCode.Decompiler.IL;
using UpstreamCSharpDecompilerEngine = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;

namespace dnSpy.Decompiler.ILSpy.Core.ILAst {
	/// <summary>
	/// What an <see cref="UpstreamILAstLanguage"/> shows
	/// </summary>
	enum UpstreamILAstView {
		/// <summary>
		/// The IL with the types the upstream decompiler infers for the stack slots
		/// </summary>
		TypedIL,

		/// <summary>
		/// The ILAst as read from the IL
		/// </summary>
		Unoptimized,

		/// <summary>
		/// The ILAst after the IL transforms the C# decompiler runs, with a stepper
		/// </summary>
		Transformed,
	}

	/// <summary>
	/// The upstream decompiler's ILAst (debug builds, like dnSpy's other ILAst view). It decompiles with the C#
	/// language's settings, images and type systems.
	/// </summary>
	sealed class UpstreamILAstLanguage : DecompilerBase {
		public const string ContentTypeName = ContentTypesInternal.ILAstILSpy;

		readonly UpstreamCSharpLanguage csharp;
		readonly UpstreamILAstView view;
		readonly object stepperLock = new object();
		Stepper stepper = new Stepper();
		MethodDef? stepperMethod;
		int stepLimit = int.MaxValue;
		MethodDef? stepLimitMethod;

		public UpstreamILAstLanguage(UpstreamCSharpLanguage csharp, UpstreamILAstView view) {
			this.csharp = csharp ?? throw new ArgumentNullException(nameof(csharp));
			this.view = view;
		}

		/// <summary>
		/// The languages, in the order they're shown
		/// </summary>
		public static IEnumerable<UpstreamILAstLanguage> Create(UpstreamCSharpLanguage csharp) {
			yield return new UpstreamILAstLanguage(csharp, UpstreamILAstView.TypedIL);
			yield return new UpstreamILAstLanguage(csharp, UpstreamILAstView.Unoptimized);
			yield return new UpstreamILAstLanguage(csharp, UpstreamILAstView.Transformed);
		}

		public UpstreamILAstView View => view;
		// The output depends on the C# language's settings
		public override DecompilerSettingsBase Settings => csharp.LanguageSettings;
		public override string ContentTypeString => ContentTypeName;
		public override string GenericNameUI => "ILAst";
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_ILAST_ILSPY;
		public override string FileExtension => ".il";
		public override double OrderUI => DecompilerConstants.ILAST_ILSPY_DEBUG_ORDERUI + (int)view;

		public override string UniqueNameUI =>
			view switch {
				UpstreamILAstView.TypedIL => "Typed IL",
				UpstreamILAstView.Unoptimized => "ILAst (unoptimized)",
				_ => "ILAst",
			};

		public override Guid UniqueGuid => view switch {
			UpstreamILAstView.TypedIL => new Guid("A06DC5F0-BAB0-411E-A735-235A1C49F0AE"),
			UpstreamILAstView.Unoptimized => new Guid("F67CCDDE-293E-4D7F-B87C-84DC1B3110E1"),
			_ => new Guid("B1F782FF-6947-458C-80CB-D7D652355558"),
		};

		#region Stepper
		/// <summary>
		/// The steps of the last method that was decompiled on its own with all transforms
		/// </summary>
		public Stepper Stepper {
			get {
				lock (stepperLock)
					return stepper;
			}
		}

		/// <summary>
		/// The method of <see cref="Stepper"/>
		/// </summary>
		public MethodDef? StepperMethod {
			get {
				lock (stepperLock)
					return stepperMethod;
			}
		}

		/// <summary>
		/// Raised (on the decompiler's thread) when <see cref="Stepper"/> was replaced
		/// </summary>
		public event EventHandler? StepperUpdated;

		/// <summary>
		/// Called with the output of a method whose steps are recorded: dnSpy mustn't cache it, the stepper shows the steps
		/// of the method that was decompiled last (the UI disables the cache)
		/// </summary>
		public Action<IDecompilerOutput>? DisableOutputCaching { get; set; }

		/// <summary>
		/// Shows the state before step <paramref name="limit"/> of <see cref="StepperMethod"/> the next time it's
		/// decompiled (the other methods aren't affected). <see cref="int.MaxValue"/> shows all steps.
		/// </summary>
		public void SetStepLimit(int limit) {
			lock (stepperLock) {
				stepLimitMethod = limit == int.MaxValue ? null : stepperMethod;
				stepLimit = stepLimitMethod is null ? int.MaxValue : limit;
			}
		}

		int GetStepLimit(MethodDef method) {
			lock (stepperLock)
				return method == stepLimitMethod ? stepLimit : int.MaxValue;
		}

		void OnFullRun(MethodDef method, Stepper newStepper) {
			lock (stepperLock) {
				stepper = newStepper;
				stepperMethod = method;
				// The step limit belongs to the method it was chosen for
				stepLimitMethod = null;
				stepLimit = int.MaxValue;
			}
			StepperUpdated?.Invoke(this, EventArgs.Empty);
		}
		#endregion

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) =>
			DecompileMethod(method, output, ctx, canStep: true);

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			WriteComment(output, property.FullName, property);
			output.WriteLine();
			DecompileMethods(property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods), output, ctx);
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			WriteComment(output, ev.FullName, ev);
			output.WriteLine();
			DecompileMethods(new[] { ev.AddMethod, ev.RemoveMethod, ev.InvokeMethod }.Where(a => a is not null)!, output, ctx);
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) =>
			WriteComment(output, field.FullName, field);

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			WriteComment(output, type.FullName, type);
			output.WriteLine();
			DecompileMethods(type.Methods, output, ctx);
			foreach (var nested in type.NestedTypes) {
				ctx.CancellationToken.ThrowIfCancellationRequested();
				Decompile(nested, output, ctx);
			}
		}

		void DecompileMethods(IEnumerable<MethodDef> methods, IDecompilerOutput output, DecompilationContext ctx) {
			foreach (var method in methods) {
				ctx.CancellationToken.ThrowIfCancellationRequested();
				DecompileMethod(method, output, ctx, canStep: false);
				output.WriteLine();
			}
		}

		/// <param name="canStep">The method is decompiled on its own: its steps are shown by the stepper</param>
		void DecompileMethod(MethodDef method, IDecompilerOutput output, DecompilationContext ctx, bool canStep) {
			WriteComment(output, method.FullName, method);
			if (!method.HasBody)
				return;
			var module = method.Module;
			csharp.CheckModified(module, new[] { method }, ctx);
			var decompilationSettings = csharp.LanguageSettings.CreateDecompilationSettings();
			int limit = canStep ? GetStepLimit(method) : int.MaxValue;
			if (canStep && view == UpstreamILAstView.Transformed)
				DisableOutputCaching?.Invoke(output);
			BridgedModule bridged;
			try {
				bridged = csharp.Cache.Get(module);
			}
			catch (ModuleBridgeException ex) {
				WriteComment(output, "The module can't be decompiled: " + ex.Message);
				return;
			}
			if (UpstreamILAst.GetBody(bridged, method) is not var (handle, body)) {
				WriteComment(output, "The method's body isn't part of the image that was given to the decompiler.");
				return;
			}

			BigStackRunner.Run(() => {
				var typeSystem = csharp.TypeSystems.Get(bridged, decompilationSettings);
				var debugInfo = decompilationSettings.UseDebugSymbols ? DnlibDebugInfoProvider.TryCreate(bridged) : null;
				var reader = new ILReader(typeSystem.MainModule) {
					UseDebugSymbols = decompilationSettings.UseDebugSymbols,
					UseRefLocalsForAccurateOrderOfEvaluation = decompilationSettings.UseRefLocalsForAccurateOrderOfEvaluation,
					DebugInfo = debugInfo,
				};
				var genericContext = UpstreamILAst.GetGenericContext(typeSystem.MainModule.GetDefinition(handle));
				var references = new ReferenceFactory(csharp.Cache, module);
				var textOutput = new DnSpyTextOutput(output, references, new SourceVariableTable(csharp.Cache, references.TypeSigConverter));
				if (view == UpstreamILAstView.TypedIL) {
					reader.WriteTypedIL(handle, body, textOutput, genericContext, ctx.CancellationToken);
					return;
				}

				var function = reader.ReadIL(handle, body, genericContext, ILFunctionKind.TopLevelFunction, ctx.CancellationToken);
				if (view == UpstreamILAstView.Transformed) {
					var newStepper = new Stepper { StepLimit = limit };
					var decompiler = new UpstreamCSharpDecompilerEngine(typeSystem, decompilationSettings) {
						CancellationToken = ctx.CancellationToken,
						DebugInfoProvider = debugInfo,
					};
					var context = decompiler.CreateILTransformContext(function);
					Exception? error = null;
					try {
						UpstreamILAst.RunTransforms(function, context, newStepper);
					}
					catch (StepLimitReachedException) {
					}
					catch (Exception ex) when (ex is not OperationCanceledException) {
						error = ex;
					}
					if (canStep && limit == int.MaxValue)
						OnFullRun(method, newStepper);
					if (newStepper.LimitReachedStep is Stepper.Node limitStep)
						WriteComment(output, "Before step " + limitStep.Description);
					if (error is not null) {
						WriteComment(output, "A transform failed: " + error);
						WriteComment(output, "The ILAst after the failure:");
					}
				}
				function.WriteTo(textOutput, new ILAstWritingOptions {
					UseFieldSugar = true,
					UseLogicOperationSugar = true,
				});
				textOutput.WriteLine();
			});
		}

		void WriteComment(IDecompilerOutput output, string text, object? reference = null) {
			foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
				WriteCommentBegin(output, true);
				if (reference is null)
					output.Write(line, BoxedTextColor.Comment);
				else
					output.Write(line, reference, DecompilerReferenceFlags.Definition, BoxedTextColor.Comment);
				WriteCommentEnd(output, true);
				output.WriteLine();
			}
		}
	}
}
#endif
