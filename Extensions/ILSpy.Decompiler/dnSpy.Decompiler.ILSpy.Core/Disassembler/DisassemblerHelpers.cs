// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.DotNet.Pdb;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	public enum ILNameSyntax
	{
		/// <summary>
		/// class/valuetype + TypeName (built-in types use keyword syntax)
		/// </summary>
		Signature,
		/// <summary>
		/// Like signature, but always refers to type parameters using their position
		/// </summary>
		SignatureNoNamedTypeParameters,
		/// <summary>
		/// [assembly]Full.Type.Name (even for built-in types)
		/// </summary>
		TypeName,
		/// <summary>
		/// Name (but built-in types use keyword syntax)
		/// </summary>
		ShortTypeName
	}

	public static class DisassemblerHelpers
	{
		static readonly char[] _validNonLetterIdentifierCharacter = { '_', '$', '@', '?', '`', '.' };

		const int OPERAND_ALIGNMENT = 10;

		static DisassemblerHelpers()
		{
			spaces = new string[OPERAND_ALIGNMENT];
			for (int i = 0; i < spaces.Length; i++)
				spaces[i] = new string(' ', i);
		}
		static readonly string[] spaces;

		public static void WriteOffsetReference(IDecompilerOutput writer, Instruction instruction, MethodDef method, object data = null)
		{
			data ??= BoxedTextColor.Label;
			var r = instruction == null ? null : method == null ? (object)instruction : new InstructionReference(method, instruction);
			writer.Write(DisassemblerUtils.OffsetToString(instruction.GetOffset()), r, DecompilerReferenceFlags.None, data);
		}

		public static void WriteTo(this ExceptionHandler exceptionHandler, IDecompilerOutput writer, StringBuilder sb, MethodDef method)
		{
			writer.Write(".try", BoxedTextColor.Keyword);
			writer.Write(" ", BoxedTextColor.Text);
			WriteOffsetReference(writer, exceptionHandler.TryStart, method);
			writer.Write("-", BoxedTextColor.Operator);
			WriteOffsetReference(writer, exceptionHandler.TryEnd, method);
			writer.Write(" ", BoxedTextColor.Text);
			writer.Write(exceptionHandler.HandlerType.ToString(), BoxedTextColor.Keyword);
			if (exceptionHandler.FilterStart != null) {
				writer.Write(" ", BoxedTextColor.Text);
				WriteOffsetReference(writer, exceptionHandler.FilterStart, method);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("handler", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			if (exceptionHandler.CatchType != null) {
				writer.Write(" ", BoxedTextColor.Text);
				exceptionHandler.CatchType.WriteTo(writer, sb);
			}
			writer.Write(" ", BoxedTextColor.Text);
			WriteOffsetReference(writer, exceptionHandler.HandlerStart, method);
			writer.Write("-", BoxedTextColor.Operator);
			WriteOffsetReference(writer, exceptionHandler.HandlerEnd, method);
		}

		internal static void WriteTo(this Instruction instruction, IDecompilerOutput writer, StringBuilder sb, DisassemblerOptions options, long baseOffs, IInstructionBytesReader byteReader, MethodDef method, InstructionOperandConverter instructionOperandConverter, PdbAsyncMethodCustomDebugInfo pdbAsyncInfo, out int startLocation)
		{
			var numberFormatter = NumberFormatter.GetCSharpInstance(hex: options.HexadecimalNumbers, upper: true);
			if (options.ShowPdbInfo) {
				var seqPoint = instruction.SequencePoint;
				if (seqPoint != null) {
					writer.Write("/* ", BoxedTextColor.Comment);
					int leftStart = writer.NextPosition;
					writer.Write("(", BoxedTextColor.Comment);
					const int HIDDEN = 0xFEEFEE;
					if (seqPoint.StartLine == HIDDEN)
						writer.Write("hidden", BoxedTextColor.Comment);
					else {
						writer.Write(seqPoint.StartLine.ToString(), BoxedTextColor.Comment);
						writer.Write(",", BoxedTextColor.Comment);
						writer.Write(seqPoint.StartColumn.ToString(), BoxedTextColor.Comment);
					}
					int start = writer.NextPosition;
					writer.Write(")", BoxedTextColor.Comment);
					writer.AddBracePair(new TextSpan(leftStart, 1), new TextSpan(start, 1), CodeBracesRangeFlags.Parentheses);
					writer.Write("-", BoxedTextColor.Comment);
					leftStart = writer.NextPosition;
					writer.Write("(", BoxedTextColor.Comment);
					if (seqPoint.EndLine == HIDDEN)
						writer.Write("hidden", BoxedTextColor.Comment);
					else {
						writer.Write(seqPoint.EndLine.ToString(), BoxedTextColor.Comment);
						writer.Write(",", BoxedTextColor.Comment);
						writer.Write(seqPoint.EndColumn.ToString(), BoxedTextColor.Comment);
					}
					start = writer.NextPosition;
					writer.Write(")", BoxedTextColor.Comment);
					writer.AddBracePair(new TextSpan(leftStart, 1), new TextSpan(start, 1), CodeBracesRangeFlags.Parentheses);
					writer.Write(" ", BoxedTextColor.Comment);
					writer.Write(seqPoint.Document.Url, BoxedTextColor.Comment);
					writer.Write(" */", BoxedTextColor.Comment);
					writer.WriteLine();
				}
				if (pdbAsyncInfo != null) {
					if (pdbAsyncInfo.CatchHandlerInstruction == instruction)
						writer.WriteLine("/* Catch Handler */", BoxedTextColor.Comment);
					var asyncStepInfos = pdbAsyncInfo.StepInfos;
					for (int i = 0; i < asyncStepInfos.Count; i++) {
						var info = asyncStepInfos[i];
						if (info.YieldInstruction == instruction)
							writer.WriteLine("/* Yield Instruction */", BoxedTextColor.Comment);
						if (info.BreakpointInstruction == instruction)
							writer.WriteLine("/* Resume Instruction */", BoxedTextColor.Comment);
					}
				}
			}
			if ((options.ShowTokenAndRvaComments || options.ShowILBytes)) {
				writer.Write("/* ", BoxedTextColor.Comment);

				bool needSpace = false;

				if (options.ShowTokenAndRvaComments) {
					ulong fileOffset = (ulong)baseOffs + instruction.Offset;
					var hexOffsetString = $"0x{fileOffset:X8}";
					bool orig = byteReader?.IsOriginalBytes == true;
					if (orig)
						writer.Write(hexOffsetString, new AddressReference(options.OwnerModule?.Location, false, fileOffset, (ulong)instruction.GetSize()), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
					else
						writer.Write(hexOffsetString, BoxedTextColor.Comment);
					needSpace = true;
				}

				if (options.ShowILBytes) {
					if (needSpace)
						writer.Write(" ", BoxedTextColor.Comment);
					if (byteReader == null)
						writer.Write("??", BoxedTextColor.Comment);
					else {
						int size = instruction.GetSize();
						for (int i = 0; i < size; i++) {
							var b = byteReader.ReadByte();
							if (b < 0)
								writer.Write("??", BoxedTextColor.Comment);
							else
								writer.Write($"{b:X2}", BoxedTextColor.Comment);
						}
						// Most instructions should be at most 5 bytes in length, but use 6 since
						// ldftn/ldvirtftn are 6 bytes long. The longest instructions are those with
						// 8 byte operands, ldc.i8 and ldc.r8: 9 bytes.
						const int MIN_BYTES = 6;
						for (int i = size; i < MIN_BYTES; i++)
							writer.Write("  ", BoxedTextColor.Comment);
					}
				}

				writer.Write(" */", BoxedTextColor.Comment);
				writer.Write(" ", BoxedTextColor.Text);
			}
			startLocation = writer.NextPosition;
			writer.Write(DisassemblerUtils.OffsetToString(instruction.GetOffset()), new InstructionReference(method, instruction), DecompilerReferenceFlags.Definition, BoxedTextColor.Label);
			writer.Write(":", BoxedTextColor.Punctuation);
			writer.Write(" ", BoxedTextColor.Text);
			writer.Write(instruction.OpCode.Name, instruction.OpCode, DecompilerReferenceFlags.None, BoxedTextColor.OpCode);
			if (ShouldHaveOperand(instruction)) {
				int count = OPERAND_ALIGNMENT - instruction.OpCode.Name.Length;
				if (count <= 0)
					count = 1;
				writer.Write(spaces[count], BoxedTextColor.Text);
				if (instruction.OpCode == OpCodes.Ldtoken) {
					var member = instruction.Operand as IMemberRef;
					if (member != null && member.IsMethod) {
						writer.Write("method", BoxedTextColor.Keyword);
						writer.Write(" ", BoxedTextColor.Text);
					}
					else if (member != null && member.IsField) {
						writer.Write("field", BoxedTextColor.Keyword);
						writer.Write(" ", BoxedTextColor.Text);
					}
				}
				WriteOperand(writer, instructionOperandConverter?.Convert(instruction.Operand) ?? instruction.Operand, options.MaxStringLength, numberFormatter, sb, method);
			}

			var doc = options.GetOpCodeDocumentation?.Invoke(instruction.OpCode);
			if (doc != null) {
				writer.Write("\t", BoxedTextColor.Text);
				writer.Write("// " + doc, BoxedTextColor.Comment);
			}
		}

		static bool ShouldHaveOperand(Instruction instr)
		{
			switch (instr.OpCode.OperandType) {
			case OperandType.InlineBrTarget:
			case OperandType.InlineField:
			case OperandType.InlineI:
			case OperandType.InlineI8:
			case OperandType.InlineMethod:
			case OperandType.InlineR:
			case OperandType.InlineSig:
			case OperandType.InlineString:
			case OperandType.InlineSwitch:
			case OperandType.InlineTok:
			case OperandType.InlineType:
			case OperandType.InlineVar:
			case OperandType.ShortInlineBrTarget:
			case OperandType.ShortInlineI:
			case OperandType.ShortInlineR:
			case OperandType.ShortInlineVar:
				return true;
			case OperandType.InlineNone:
			case OperandType.InlinePhi:
			default:
				return false;
			}
		}

		static void WriteLabelList(IDecompilerOutput writer, IList<Instruction> instructions, MethodDef method)
		{
			var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
			for(int i = 0; i < instructions.Count; i++) {
				if (i != 0) {
					writer.Write(",", BoxedTextColor.Punctuation);
					writer.Write(" ", BoxedTextColor.Text);
				}
				WriteOffsetReference(writer, instructions[i], method);
			}
			bh1.Write(")");
		}

		static string ToInvariantCultureString(object value)
		{
			if (value == null)
				return "<<<NULL>>>";
			return value is IConvertible convertible
				? convertible.ToString(System.Globalization.CultureInfo.InvariantCulture)
				: value.ToString();
		}

		public static void WriteMethodTo(this IMethod method, IDecompilerOutput writer, StringBuilder sb) => writer.Write(sb, null, method);

		public static void Write(this IDecompilerOutput writer, StringBuilder sb, MethodSig sig, IMethod method = null)
		{
			if (sig == null && method != null)
				sig = method.MethodSig;
			if (sig == null)
				return;
			if (sig.ExplicitThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("explicit", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (sig.HasThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			if (sig.CallingConvention == CallingConvention.VarArg) {
				writer.Write("vararg", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			sig.RetType.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			if (method != null) {
				if (method.DeclaringType != null) {
					method.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName);
					writer.Write("::", BoxedTextColor.Operator);
				}
				if (method is MethodDef md && md.IsCompilerControlled) {
					writer.Write(Escape(method.Name + "$PST" + method.MDToken.ToInt32().ToString("X8")), method, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(method));
				}
				else {
					writer.Write(Escape(method.Name), method, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(method));
				}
			}
			if (method is MethodSpec gim && gim.GenericInstMethodSig != null) {
				var bh1 = BracePairHelper.Create(writer, "<", CodeBracesRangeFlags.AngleBrackets);
				for (int i = 0; i < gim.GenericInstMethodSig.GenericArguments.Count; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					gim.GenericInstMethodSig.GenericArguments[i].WriteTo(writer, sb);
				}
				bh1.Write(">");
			}
			var bh2 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
			var parameters = sig.GetParameters();
			for(int i = 0; i < parameters.Count; ++i) {
				if (i > 0) {
					writer.Write(",", BoxedTextColor.Punctuation);
					writer.Write(" ", BoxedTextColor.Text);
				}
				parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			}
			bh2.Write(")");
		}

		public static void WriteTo(this MethodSig sig, IDecompilerOutput writer, StringBuilder sb)
		{
			if (sig.ExplicitThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("explicit", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (sig.HasThis) {
				writer.Write("instance", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
			}
			sig.RetType.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
			var parameters = sig.GetParameters();
			for(int i = 0; i < parameters.Count; ++i) {
				if (i > 0) {
					writer.Write(",", BoxedTextColor.Punctuation);
					writer.Write(" ", BoxedTextColor.Text);
				}
				parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			}
			bh1.Write(")");
		}

		public static void WriteFieldTo(this IField field, IDecompilerOutput writer, StringBuilder sb)
		{
			if (field == null || field.FieldSig == null)
				return;
			field.FieldSig.Type.WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
			writer.Write(" ", BoxedTextColor.Text);
			field.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName);
			writer.Write("::", BoxedTextColor.Operator);
			writer.Write(Escape(field.Name), field, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(field));
		}

		static bool IsValidIdentifierCharacter(char c)
			=> char.IsLetterOrDigit(c) || _validNonLetterIdentifierCharacter.Contains(c);

		static bool IsValidIdentifier(string identifier)
		{
			if (string.IsNullOrEmpty(identifier))
				return false;

			char c = identifier[0];
			if (char.IsDigit(c))
				return false;

			// As a special case, .ctor and .cctor are valid despite starting with a dot
			if (c == '.')
				return identifier == ".ctor" || identifier == ".cctor";

			if (identifier.Contains(".."))
				return false;

			if (ilKeywords.Contains(identifier))
				return false;

			for (var i = 0; i < identifier.Length; i++) {
				if (!IsValidIdentifierCharacter(identifier[i]))
					return false;
			}
			return true;
		}

		static readonly HashSet<string> ilKeywords = BuildKeywordList(
			"abstract", "algorithm", "alignment", "ansi", "any", "arglist",
			"array", "as", "assembly", "assert", "at", "auto", "autochar", "beforefieldinit",
			"blob", "blob_object", "bool", "brnull", "brnull.s", "brzero", "brzero.s", "bstr",
			"bytearray", "byvalstr", "callmostderived", "carray", "catch", "cdecl", "cf",
			"char", "cil", "class", "clsid", "const", "currency", "custom", "date", "decimal",
			"default", "demand", "deny", "endmac", "enum", "error", "explicit", "extends", "extern",
			"false", "famandassem", "family", "famorassem", "fastcall", "fault", "field", "filetime",
			"filter", "final", "finally", "fixed", "float", "float32", "float64", "forwardref",
			"fromunmanaged", "handler", "hidebysig", "hresult", "idispatch", "il", "illegal",
			"implements", "implicitcom", "implicitres", "import", "in", "inheritcheck", "init",
			"initonly", "instance", "int", "int16", "int32", "int64", "int8", "interface", "internalcall",
			"iunknown", "lasterr", "lcid", "linkcheck", "literal", "localloc", "lpstr", "lpstruct", "lptstr",
			"lpvoid", "lpwstr", "managed", "marshal", "method", "modopt", "modreq", "native", "nested",
			"newslot", "noappdomain", "noinlining", "nomachine", "nomangle", "nometadata", "noncasdemand",
			"noncasinheritance", "noncaslinkdemand", "noprocess", "not", "not_in_gc_heap", "notremotable",
			"notserialized", "null", "nullref", "object", "objectref", "opt", "optil", "out",
			"permitonly", "pinned", "pinvokeimpl", "prefix1", "prefix2", "prefix3", "prefix4", "prefix5", "prefix6",
			"prefix7", "prefixref", "prejitdeny", "prejitgrant", "preservesig", "private", "privatescope", "protected",
			"public", "record", "refany", "reqmin", "reqopt", "reqrefuse", "reqsecobj", "request", "retval",
			"rtspecialname", "runtime", "safearray", "sealed", "sequential", "serializable", "special", "specialname",
			"static", "stdcall", "storage", "stored_object", "stream", "streamed_object", "string", "struct",
			"synchronized", "syschar", "sysstring", "tbstr", "thiscall", "tls", "to", "true", "typedref",
			"unicode", "unmanaged", "unmanagedexp", "unsigned", "unused", "userdefined", "value", "valuetype",
			"vararg", "variant", "vector", "virtual", "void", "wchar", "winapi", "with", "wrapper",

			// These are not listed as keywords in spec, but ILAsm treats them as such
			"property", "type", "flags", "codelabel", "callconv", "strict",
			// ILDasm uses these keywords for unsigned integers
			"uint8", "uint16", "uint32", "uint64"
		);

		static HashSet<string> BuildKeywordList(params string[] keywords)
		{
			HashSet<string> s = new HashSet<string>(keywords);
			foreach (var field in typeof(OpCodes).GetFields()) {
				if (field.FieldType != typeof(OpCode))
					continue;
				OpCode opCode = (OpCode)field.GetValue(null);
				if (opCode.OpCodeType != OpCodeType.Nternal)
					s.Add(opCode.Name);
			}
			return s;
		}

		internal static bool MustEscape(string identifier) {
			return !IsValidIdentifier(identifier);
		}

		public static string Escape(string identifier) {
			if (MustEscape(identifier)) {
				// The ECMA specification says that ' inside SQString should be ecaped using an octal escape sequence,
				// but we follow Microsoft's ILDasm and use \'.
				return "'" + IdentifierEscaper.Truncate(ILStringEscaper.ConvertString(identifier)
																  .Replace("'", "\\'")) + "'";
			}
			else {
				return IdentifierEscaper.Truncate(identifier);
			}
		}

		public static void WriteTo(this TypeSig type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax = ILNameSyntax.Signature) => type.WriteTo(writer, sb, syntax, 0);

		const int MAX_CONVERTTYPE_DEPTH = 50;
		public static void WriteTo(this TypeSig type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax, int depth)
		{
			if (depth++ > MAX_CONVERTTYPE_DEPTH)
				return;
			ILNameSyntax syntaxForElementTypes = syntax == ILNameSyntax.SignatureNoNamedTypeParameters ? syntax : ILNameSyntax.Signature;
			if (type is PinnedSig sig) {
				sig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("pinned", BoxedTextColor.Keyword);
			} else if (type is ArraySig arraySig) {
				arraySig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
				for (int i = 0; i < arraySig.Rank; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}

					if (i < arraySig.LowerBounds.Count || i < arraySig.Sizes.Count) {
						int lower = i < arraySig.LowerBounds.Count ? arraySig.LowerBounds[i] : 0;
						writer.Write(lower.ToString(), BoxedTextColor.Number);
						writer.Write("...", BoxedTextColor.Operator);
						if (i < arraySig.Sizes.Count)
							writer.Write((lower + (int)arraySig.Sizes[i] - 1).ToString(), BoxedTextColor.Number);
					}
				}
				bh1.Write("]");
			} else if (type is SZArraySig at) {
				at.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
				bh1.Write("]");
			} else if (type is GenericSig genericSig) {
				if (genericSig.IsMethodVar)
					writer.Write("!!", BoxedTextColor.Operator);
				else
					writer.Write("!", BoxedTextColor.Operator);
				sb.Clear();
				string typeName = FullNameFactory.Name(genericSig, false, sb);
				if (string.IsNullOrEmpty(typeName) || typeName[0] == '!' || syntax == ILNameSyntax.SignatureNoNamedTypeParameters)
					writer.Write(genericSig.Number.ToString(), BoxedTextColor.Number);
				else
					writer.Write(Escape(typeName), genericSig.GenericParam, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
			} else if (type is ByRefSig refSig) {
				refSig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write("&", BoxedTextColor.Operator);
			} else if (type is PtrSig ptrSig) {
				ptrSig.Next.WriteTo(writer, sb, syntaxForElementTypes, depth);
				writer.Write("*", BoxedTextColor.Operator);
			} else if (type is GenericInstSig instSig) {
				instSig.GenericType.WriteTo(writer, sb, syntaxForElementTypes, depth);
				var bh1 = BracePairHelper.Create(writer, "<", CodeBracesRangeFlags.AngleBrackets);
				var arguments = instSig.GenericArguments;
				for (int i = 0; i < arguments.Count; i++) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					arguments[i].WriteTo(writer, sb, syntaxForElementTypes, depth);
				}
				bh1.Write(">");
			} else if (type is CModOptSig optSig) {
				optSig.Next.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("modopt", BoxedTextColor.Keyword);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				optSig.Modifier.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
				bh1.Write(")");
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (type is CModReqdSig reqdSig) {
				reqdSig.Next.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("modreq", BoxedTextColor.Keyword);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				reqdSig.Modifier.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
				bh1.Write(")");
				writer.Write(" ", BoxedTextColor.Text);
			}
			else if (type is SentinelSig)
				writer.Write("...", BoxedTextColor.Operator);
			else if (type is FnPtrSig fnPtrSig) {
				writer.Write("method", BoxedTextColor.Keyword);
				writer.Write(" ", BoxedTextColor.Text);
				if (fnPtrSig.MethodSig.ExplicitThis) {
					writer.Write("instance", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
					writer.Write("explicit", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				} else if (fnPtrSig.MethodSig.HasThis) {
					writer.Write("instance", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}
				var callingConvention = fnPtrSig.MethodSig.CallingConvention & CallingConvention.Mask;
				if (callingConvention != CallingConvention.Default) {
					var callConv = callingConvention switch {
						CallingConvention.C => "unmanaged cdecl",
						CallingConvention.StdCall => "unmanaged stdcall",
						CallingConvention.ThisCall => "unmanaged thiscall",
						CallingConvention.FastCall => "unmanaged fastcall",
						CallingConvention.VarArg => "vararg",
						CallingConvention.Unmanaged => "unmanaged",
						_ => callingConvention.ToString().ToLowerInvariant()
					};
					writer.Write(callConv, BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}
				fnPtrSig.MethodSig.RetType.WriteTo(writer, sb, syntax, depth);
				writer.Write(" ", BoxedTextColor.Text);
				writer.Write("*", BoxedTextColor.Operator);
				var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
				var parameters = fnPtrSig.MethodSig.GetParameters();
				for (int i = 0; i < parameters.Count; ++i) {
					if (i > 0) {
						writer.Write(",", BoxedTextColor.Punctuation);
						writer.Write(" ", BoxedTextColor.Text);
					}
					parameters[i].WriteTo(writer, sb, ILNameSyntax.SignatureNoNamedTypeParameters);
				}
				bh1.Write(")");
			}
			else if (type is TypeDefOrRefSig tdrs) {
				ThreeState isVT;
				if (tdrs is ClassSig)
					isVT = ThreeState.No;
				else if (tdrs is ValueTypeSig)
					isVT = ThreeState.Yes;
				else if (tdrs is CorLibTypeSig corLibTypeSig)
					isVT = IsValueType(corLibTypeSig);
				else
					isVT = ThreeState.Unknown;
				WriteTo(tdrs.TypeDefOrRef, writer, sb, syntax, isVT, depth);
			}
		}

		public static void WriteTo(this ITypeDefOrRef type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax = ILNameSyntax.Signature) => type.WriteTo(writer, sb, syntax, ThreeState.Unknown, 0);

		internal static void WriteTo(this ITypeDefOrRef type, IDecompilerOutput writer, StringBuilder sb, ILNameSyntax syntax, ThreeState isValueType, int depth)
		{
			if (depth++ > MAX_CONVERTTYPE_DEPTH || type == null)
				return;
			if (type is TypeSpec ts) {
				WriteTo(ts.TypeSig, writer, sb, syntax, depth);
				return;
			}
			sb.Clear();
			string typeFullName = FullNameFactory.FullName(type, false, null, sb);
			string typeName = type.Name.String;
			TypeSig typeSig = null;
			string name = type.DefinitionAssembly.IsCorLib() ? PrimitiveTypeName(typeFullName, type.Module, out typeSig) : null;
			if (syntax == ILNameSyntax.ShortTypeName) {
				if (name != null)
					WriteKeyword(writer, name, typeSig.ToTypeDefOrRef());
				else
					writer.Write(Escape(typeName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
			} else if ((syntax == ILNameSyntax.Signature || syntax == ILNameSyntax.SignatureNoNamedTypeParameters) && name != null) {
				WriteKeyword(writer, name, typeSig.ToTypeDefOrRef());
			} else {
				if (syntax == ILNameSyntax.Signature || syntax == ILNameSyntax.SignatureNoNamedTypeParameters) {
					bool isVT;
					if (isValueType != ThreeState.Unknown)
						isVT = isValueType == ThreeState.Yes;
					else
						isVT = DisassemblerUtils.IsValueType(type);
					writer.Write(isVT ? "valuetype" : "class", BoxedTextColor.Keyword);
					writer.Write(" ", BoxedTextColor.Text);
				}

				if (type.DeclaringType != null) {
					type.DeclaringType.WriteTo(writer, sb, ILNameSyntax.TypeName, ThreeState.Unknown, depth);
					writer.Write("/", BoxedTextColor.Operator);
					WriteNamespace(writer, type.Namespace, type.DefinitionAssembly, sb);
					if (!string.IsNullOrEmpty(type.Namespace))
						writer.Write(".", BoxedTextColor.Operator);
					writer.Write(Escape(typeName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
				} else {
					if (!(type is TypeDef) && type.Scope != null) {
						var bh1 = BracePairHelper.Create(writer, "[", CodeBracesRangeFlags.SquareBrackets);
						writer.Write(Escape(type.Scope.GetScopeName()), type.Scope, DecompilerReferenceFlags.None, BoxedTextColor.ILModule);
						bh1.Write("]");
					}
					if (MustEscape(typeFullName))
						writer.Write(Escape(typeFullName), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
					else {
						WriteNamespace(writer, type.Namespace, type.DefinitionAssembly, sb);
						if (!string.IsNullOrEmpty(type.Namespace))
							writer.Write(".", BoxedTextColor.Operator);
						writer.Write(IdentifierEscaper.Escape(type.Name), type, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(type));
					}
				}
			}
		}

		internal static void WriteNamespace(IDecompilerOutput writer, string ns, IAssembly nsAsm, StringBuilder sb)
		{
			sb.Clear();
			var parts = ns.Split('.');
			for (int i = 0; i < parts.Length; i++) {
				if (i > 0) {
					sb.Append('.');
					writer.Write(".", BoxedTextColor.Operator);
				}
				var nsPart = parts[i];
				sb.Append(nsPart);
				if (!string.IsNullOrEmpty(nsPart)) {
					var nsRef = new NamespaceReference(nsAsm, sb.ToString());
					writer.Write(IdentifierEscaper.Escape(nsPart), nsRef, DecompilerReferenceFlags.None, BoxedTextColor.Namespace);
				}
			}
		}

		internal static void WriteKeyword(IDecompilerOutput writer, string name, ITypeDefOrRef tdr)
		{
			var parts = name.Split(' ');
			for (int i = 0; i < parts.Length; i++) {
				if (i > 0)
					writer.Write(" ", BoxedTextColor.Text);
				if (tdr != null)
					writer.Write(parts[i], tdr, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
				else
					writer.Write(parts[i], BoxedTextColor.Keyword);
			}
		}

		public static void WriteOperand(IDecompilerOutput writer, object operand, int maxStringLength, NumberFormatter numberFormatter, StringBuilder sb, MethodDef method = null)
		{
			if (operand is Instruction targetInstruction) {
				WriteOffsetReference(writer, targetInstruction, method);
				return;
			}

			if (operand is IList<Instruction> targetInstructions) {
				WriteLabelList(writer, targetInstructions, method);
				return;
			}

			if (operand is SourceLocal variable) {
				writer.Write(Escape(variable.Name), variable, DecompilerReferenceFlags.Local, BoxedTextColor.Local);
				return;
			}

			if (operand is Parameter paramRef) {
				if (string.IsNullOrEmpty(paramRef.Name)) {
					if (paramRef.IsHiddenThisParameter)
						writer.Write("<hidden-this>", paramRef, DecompilerReferenceFlags.Local, BoxedTextColor.Parameter);
					else
						writer.Write(paramRef.MethodSigIndex.ToString(), paramRef, DecompilerReferenceFlags.Local, BoxedTextColor.Parameter);
				}
				else
					writer.Write(Escape(paramRef.Name), paramRef, DecompilerReferenceFlags.Local, BoxedTextColor.Parameter);
				return;
			}

			if (operand is MemberRef memberRef) {
				if (memberRef.IsMethodRef)
					memberRef.WriteMethodTo(writer, sb);
				else
					memberRef.WriteFieldTo(writer, sb);
				return;
			}

			if (operand is MethodDef methodDef) {
				methodDef.WriteMethodTo(writer, sb);
				return;
			}

			if (operand is FieldDef fieldDef) {
				fieldDef.WriteFieldTo(writer, sb);
				return;
			}

			if (operand is ITypeDefOrRef typeRef) {
				typeRef.WriteTo(writer, sb, ILNameSyntax.TypeName);
				return;
			}

			if (operand is IMethod m) {
				m.WriteMethodTo(writer, sb);
				return;
			}

			if (operand is MethodSig sig) {
				sig.WriteTo(writer, sb);
				return;
			}

			const DecompilerReferenceFlags numberFlags = DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Hidden | DecompilerReferenceFlags.NoFollow;
			if (operand is string s) {
				int start = writer.NextPosition;
				writer.Write("\"" + ILStringEscaper.ConvertStringMaxLength(s, maxStringLength) + "\"", BoxedTextColor.String);
				int end = writer.NextPosition;
				writer.AddBracePair(new TextSpan(start, 1), new TextSpan(end - 1, 1), CodeBracesRangeFlags.DoubleQuotes);
			} else if (operand is char c) {
				writer.Write(numberFormatter.Format((int)c), (int)c, numberFlags, BoxedTextColor.Number);
			} else if (operand is float f) {
				if (f == 0) {
					if (1 / f == float.NegativeInfinity) {
						// negative zero is a special case
						writer.Write("-0.0", f, numberFlags, BoxedTextColor.Number);
					}
					else
						writer.Write("0.0", f, numberFlags, BoxedTextColor.Number);
				} else if (float.IsInfinity(f) || float.IsNaN(f)) {
					byte[] data = BitConverter.GetBytes(f);
					var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
					for (int i = 0; i < data.Length; i++) {
						if (i > 0)
							writer.Write(" ", BoxedTextColor.Text);
						writer.Write(data[i].ToString("X2"), BoxedTextColor.Number);
					}
					bh1.Write(")");
				} else {
					writer.Write(f.ToString("R", System.Globalization.CultureInfo.InvariantCulture), f, numberFlags, BoxedTextColor.Number);
				}
			} else if (operand is double val) {
				if (val == 0) {
					if (1 / val == double.NegativeInfinity) {
						// negative zero is a special case
						writer.Write("-0.0", val, numberFlags, BoxedTextColor.Number);
					}
					else
						writer.Write("0.0", val, numberFlags, BoxedTextColor.Number);
				} else if (double.IsInfinity(val) || double.IsNaN(val)) {
					byte[] data = BitConverter.GetBytes(val);
					var bh1 = BracePairHelper.Create(writer, "(", CodeBracesRangeFlags.Parentheses);
					for (int i = 0; i < data.Length; i++) {
						if (i > 0)
							writer.Write(" ", BoxedTextColor.Text);
						writer.Write(data[i].ToString("X2"), BoxedTextColor.Number);
					}
					bh1.Write(")");
				} else {
					writer.Write(val.ToString("R", System.Globalization.CultureInfo.InvariantCulture), val, numberFlags, BoxedTextColor.Number);
				}
			} else if (operand is bool b) {
				writer.Write(b ? "true" : "false", BoxedTextColor.Keyword);
			} else {
				if (operand is null)
					writer.Write("<null>", BoxedTextColor.Error);
				else {
					switch (operand) {
					case int v:
						s = numberFormatter.Format(v);
						break;
					case uint v:
						s = numberFormatter.Format(v);
						break;
					case long v:
						s = numberFormatter.Format(v);
						break;
					case ulong v:
						s = numberFormatter.Format(v);
						break;
					case byte v:
						s = numberFormatter.Format(v);
						break;
					case ushort v:
						s = numberFormatter.Format(v);
						break;
					case short v:
						s = numberFormatter.Format(v);
						break;
					case sbyte v:
						s = numberFormatter.Format(v);
						break;
					default:
						s = ToInvariantCultureString(operand);
						break;
					}
					writer.Write(s, operand, numberFlags, CSharpMetadataTextColorProvider.Instance.GetColor(operand));
				}
			}
		}

		public static string PrimitiveTypeName(string fullName, ModuleDef module, out TypeSig typeSig)
		{
			var corLibTypes = module?.CorLibTypes;
			typeSig = null;
			switch (fullName) {
				case "System.SByte":
					if (corLibTypes != null)
						typeSig = corLibTypes.SByte;
					return "int8";
				case "System.Int16":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int16;
					return "int16";
				case "System.Int32":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int32;
					return "int32";
				case "System.Int64":
					if (corLibTypes != null)
						typeSig = corLibTypes.Int64;
					return "int64";
				case "System.Byte":
					if (corLibTypes != null)
						typeSig = corLibTypes.Byte;
					return "uint8";
				case "System.UInt16":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt16;
					return "uint16";
				case "System.UInt32":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt32;
					return "uint32";
				case "System.UInt64":
					if (corLibTypes != null)
						typeSig = corLibTypes.UInt64;
					return "uint64";
				case "System.Single":
					if (corLibTypes != null)
						typeSig = corLibTypes.Single;
					return "float32";
				case "System.Double":
					if (corLibTypes != null)
						typeSig = corLibTypes.Double;
					return "float64";
				case "System.Void":
					if (corLibTypes != null)
						typeSig = corLibTypes.Void;
					return "void";
				case "System.Boolean":
					if (corLibTypes != null)
						typeSig = corLibTypes.Boolean;
					return "bool";
				case "System.String":
					if (corLibTypes != null)
						typeSig = corLibTypes.String;
					return "string";
				case "System.Char":
					if (corLibTypes != null)
						typeSig = corLibTypes.Char;
					return "char";
				case "System.Object":
					if (corLibTypes != null)
						typeSig = corLibTypes.Object;
					return "object";
				case "System.IntPtr":
					if (corLibTypes != null)
						typeSig = corLibTypes.IntPtr;
					return "native int";
				case "System.UIntPtr":
					if (corLibTypes != null)
						typeSig = corLibTypes.UIntPtr;
					return "native unsigned int";
				case "System.TypedReference":
					if (corLibTypes != null)
						typeSig = corLibTypes.TypedReference;
					return "typedref";
				default:
					return null;
			}
		}

		static ThreeState IsValueType(CorLibTypeSig corlib) {
			switch (corlib.ElementType) {
			case ElementType.Void:
			case ElementType.Boolean:
			case ElementType.Char:
			case ElementType.I1:
			case ElementType.U1:
			case ElementType.I2:
			case ElementType.U2:
			case ElementType.I4:
			case ElementType.U4:
			case ElementType.I8:
			case ElementType.U8:
			case ElementType.R4:
			case ElementType.R8:
			case ElementType.TypedByRef:
			case ElementType.I:
			case ElementType.U:
			case ElementType.R:
				return ThreeState.Yes;
			case ElementType.String:
			case ElementType.Object:
				return ThreeState.No;
			default:
				return ThreeState.Unknown;
			}
		}
	}

	enum ThreeState : byte {
		Unknown,
		No,
		Yes,
	}
}
