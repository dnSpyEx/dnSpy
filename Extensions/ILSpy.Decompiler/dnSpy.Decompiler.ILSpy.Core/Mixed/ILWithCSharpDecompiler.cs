using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Decompiler.XmlDoc;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.IL;
using dnSpy.Decompiler.ILSpy.Core.IL;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Core.Text;
using dnSpy.Decompiler.ILSpy.Core.XmlDoc;
using dnSpy.Decompiler.Utils;
using dnSpy.Decompiler.ILSpy.Core.Disassembler;

namespace dnSpy.Decompiler.ILSpy.Core.Mixed {
	/// <summary>
	/// dnSpy.Console's "IL with C#". (dnSpy creates it in its extension, with the C# decompiler that has the user's
	/// settings.)
	/// </summary>
	sealed class DecompilerProvider : IDecompilerProvider {
		// Keep the default ctor. It's used by dnSpy.Console.exe
		public DecompilerProvider() { }

		public IEnumerable<IDecompiler> Create() {
			var csharp = new UpstreamCSharpLanguage(new Settings.UpstreamDecompilerSettings());
			yield return ILWithCSharpDecompiler.Create(DecompilerSettingsService.__Instance_DONT_USE.ILDecompilerSettings, csharp);
		}
	}

	sealed class ILWithCSharpDecompiler : DecompilerBase {
		public override DecompilerSettingsBase Settings => ilLangSettings;
		readonly ILDecompilerSettings ilLangSettings;
		/// <summary>
		/// The C# decompiler that writes the C# code
		/// </summary>
		readonly IDecompiler csharpDecompiler;

		ILWithCSharpDecompiler(ILDecompilerSettings ilLangSettings, IDecompiler csharpDecompiler) {
			this.ilLangSettings = ilLangSettings;
			this.csharpDecompiler = csharpDecompiler;
		}

		/// <summary>
		/// Creates the "IL with C#" decompiler. The C# decompiler must add the debug info of the methods it decompiles
		/// (<see cref="PredefinedCustomDataIds.DebugInfo"/>) to the output.
		/// </summary>
		/// <param name="ilLangSettings">IL settings</param>
		/// <param name="csharpDecompiler">C# decompiler</param>
		public static IDecompiler Create(ILDecompilerSettings ilLangSettings, IDecompiler csharpDecompiler) =>
			new ILWithCSharpDecompiler(ilLangSettings, csharpDecompiler ?? throw new ArgumentNullException(nameof(csharpDecompiler)));

		public override double OrderUI => DecompilerConstants.IL_WITH_CSHARP_ILSPY_ORDERUI;
		public override string ContentTypeString => ContentTypesInternal.ILWithCSharpILSpy;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_IL_WITH_CSHARP;
		public override string UniqueNameUI => "IL with C#";
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_IL;
		public override Guid UniqueGuid => DecompilerConstants.LANGUAGE_IL_WITH_CSHARP_ILSPY;
		public override string FileExtension => ".il";

		ReflectionDisassembler CreateReflectionDisassembler(IDecompilerOutput output, DecompilationContext ctx, IMemberDef member) =>
			CreateReflectionDisassembler(output, ctx, member.Module);

		ReflectionDisassembler CreateReflectionDisassembler(IDecompilerOutput output, DecompilationContext ctx, ModuleDef ownerModule) {
			var disOpts = new DisassemblerOptions(ilLangSettings.Settings.SettingsVersion, ctx.CancellationToken, ownerModule);
			if (ilLangSettings.Settings.ShowILComments)
				disOpts.GetOpCodeDocumentation = ILLanguageHelper.GetOpCodeDocumentation;
			var sb = new StringBuilder();
			if (ilLangSettings.Settings.ShowXmlDocumentation)
				disOpts.GetXmlDocComments = a => ILDecompiler.GetXmlDocComments(a, sb);
			disOpts.CreateInstructionBytesReader = m => InstructionBytesReader.Create(m, ctx.IsBodyModified is not null && ctx.IsBodyModified(m));
			disOpts.ShowTokenAndRvaComments = ilLangSettings.Settings.ShowTokenAndRvaComments;
			disOpts.ShowILBytes = ilLangSettings.Settings.ShowILBytes;
			disOpts.SortMembers = ilLangSettings.Settings.SortMembers;
			disOpts.ShowPdbInfo = ilLangSettings.Settings.ShowPdbInfo;
			disOpts.MaxStringLength = ilLangSettings.Settings.MaxStringLength;
			disOpts.HexadecimalNumbers = ilLangSettings.Settings.HexadecimalNumbers;
			return new ReflectionDisassembler(output, disOpts, new CustomMethodBodyDisassembler(output, true, disOpts, sb, def => GetDebugInfo(ctx, def, sb)), sb);
		}

		SourceStatementProvider GetDebugInfo(DecompilationContext ctx, MethodDef method, StringBuilder sb) {
			var output = new DecompilerOutputImpl(sb);

			MethodDebugInfo debugInfo;
			if (StateMachineHelpers.TryGetKickoffMethod(method, out var containingMethod)) {
				var info = TryDecompileCode(containingMethod, ctx, output);
				if (info.stateMachineDebugInfo is null || info.stateMachineDebugInfo.Method != method) {
					// The decompiler can't decompile the iterator / async method, try again,
					// but only decompile the MoveNext method
					info = TryDecompileCode(method, ctx, output);
				}
				debugInfo = info.stateMachineDebugInfo ?? info.debugInfo;
			}
			else
				debugInfo = TryDecompileCode(method, ctx, output).debugInfo;

			if (debugInfo is null)
				return default;
			return new SourceStatementProvider(output.ToString(), debugInfo);
		}

		(MethodDebugInfo debugInfo, MethodDebugInfo? stateMachineDebugInfo) TryDecompileCode(MethodDef method, DecompilationContext ctx, DecompilerOutputImpl output) {
			output.Initialize(method);
			// A compiler-generated method (e.g. the MoveNext method of an async method or iterator) is decompiled as is
			csharpDecompiler.Decompile(method, output, ctx);
			return output.TryGetMethodDebugInfo();
		}

		sealed class CustomMethodBodyDisassembler : MethodBodyDisassembler {
			SourceStatementProvider _statementProvider;
			readonly Func<MethodDef, SourceStatementProvider> createSourceStatementProvider;

			public CustomMethodBodyDisassembler(IDecompilerOutput output, bool detectControlStructure, DisassemblerOptions options, StringBuilder stringBuilder, Func<MethodDef, SourceStatementProvider> createSourceStatementProvider)
				: base(output, detectControlStructure, options, stringBuilder) => this.createSourceStatementProvider = createSourceStatementProvider;


			public override void Disassemble(MethodDef method, MethodDebugInfoBuilder builder, InstructionOperandConverter instructionOperandConverter) {
				_statementProvider = createSourceStatementProvider(method);
				base.Disassemble(method, builder, instructionOperandConverter);
			}

			protected override int WriteInstruction(InstructionOperandConverter instructionOperandConverter, long baseOffs, IInstructionBytesReader byteReader, PdbAsyncMethodCustomDebugInfo pdbAsyncInfo, MethodDef method, Instruction inst) {
				var statement = _statementProvider.GetStatement(inst.Offset);

				if (statement.line is null)
					return base.WriteInstruction(instructionOperandConverter, baseOffs, byteReader, pdbAsyncInfo, method, inst);

				output.WriteLine();

				Debug.Assert(statement.span.End <= statement.line.Length);
				int pos = 0;
				while (pos < statement.line.Length) {
					int nextLineOffset;
					int eol = statement.line.IndexOf('\n', pos);
					if (eol < 0) {
						eol = statement.line.Length;
						nextLineOffset = eol;
					}
					else {
						nextLineOffset = eol + 1;
						if (eol > 0 && statement.line[eol - 1] == '\r')
							eol--;
					}

					output.Write("// ", BoxedTextColor.XmlDocCommentText);
					output.Write(statement.line, pos, eol - pos, BoxedTextColor.XmlDocCommentText);
					output.WriteLine();

					if (pos == 0) {
						int nonSpace = FindNonSpace(statement.line, pos, eol);
						int stmtEnd = Math.Min(eol, statement.span.End);
						if (!(nonSpace >= statement.span.Start && stmtEnd == eol)) {
							output.Write("// ", BoxedTextColor.XmlDocCommentText);
							output.Write(new string(' ', statement.span.Start - pos), BoxedTextColor.XmlDocCommentText);
							output.Write(new string('^', stmtEnd - statement.span.Start), BoxedTextColor.XmlDocCommentText);
							output.WriteLine();
						}
					}

					pos = nextLineOffset;
				}

				output.WriteLine();

				return base.WriteInstruction(instructionOperandConverter, baseOffs, byteReader, pdbAsyncInfo, method, inst);
			}

			static int FindNonSpace(string lines, int pos, int end) {
				while (pos < end) {
					if (!char.IsWhiteSpace(lines[pos]))
						return pos;
					pos++;
				}
				return -1;
			}
		}

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			var dis = CreateReflectionDisassembler(output, ctx, method);
			dis.DisassembleMethod(method, true);
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			var dis = CreateReflectionDisassembler(output, ctx, field);
			dis.DisassembleField(field, false);
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			ReflectionDisassembler rd = CreateReflectionDisassembler(output, ctx, property);
			rd.DisassembleProperty(property, addLineSep: true);
			if (property.GetMethod is not null) {
				output.WriteLine();
				rd.DisassembleMethod(property.GetMethod, true);
			}
			if (property.SetMethod is not null) {
				output.WriteLine();
				rd.DisassembleMethod(property.SetMethod, true);
			}
			foreach (var m in property.OtherMethods) {
				output.WriteLine();
				rd.DisassembleMethod(m, true);
			}
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			ReflectionDisassembler rd = CreateReflectionDisassembler(output, ctx, ev);
			rd.DisassembleEvent(ev, addLineSep: true);
			if (ev.AddMethod is not null) {
				output.WriteLine();
				rd.DisassembleMethod(ev.AddMethod, true);
			}
			if (ev.RemoveMethod is not null) {
				output.WriteLine();
				rd.DisassembleMethod(ev.RemoveMethod, true);
			}
			foreach (var m in ev.OtherMethods) {
				output.WriteLine();
				rd.DisassembleMethod(m, true);
			}
		}

		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) {
			var dis = CreateReflectionDisassembler(output, ctx, type);
			dis.DisassembleType(type, true);
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			output.WriteLine("// " + asm.ManifestModule.Location, BoxedTextColor.Comment);
			PrintEntryPoint(asm.ManifestModule, output);
			output.WriteLine();

			ReflectionDisassembler rd = CreateReflectionDisassembler(output, ctx, asm.ManifestModule);
			rd.WriteAssemblyHeader(asm);
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			output.WriteLine("// " + mod.Location, BoxedTextColor.Comment);
			PrintEntryPoint(mod, output);
			output.WriteLine();

			ReflectionDisassembler rd = CreateReflectionDisassembler(output, ctx, mod);
			output.WriteLine();
			rd.WriteModuleHeader(mod);
		}

		protected override void TypeToString(IDecompilerOutput output, ITypeDefOrRef? t, bool includeNamespace, IHasCustomAttribute? attributeProvider = null) =>
			t.WriteTo(output, new StringBuilder(), includeNamespace ? ILNameSyntax.TypeName : ILNameSyntax.ShortTypeName);

		public override void WriteToolTip(ITextColorWriter output, IMemberRef member, IHasCustomAttribute? typeAttributes) {
			if (!(member is ITypeDefOrRef) && ILDecompilerUtils.Write(TextColorWriterToDecompilerOutput.Create(output), member))
				return;

			base.WriteToolTip(output, member, typeAttributes);
		}
	}
}
