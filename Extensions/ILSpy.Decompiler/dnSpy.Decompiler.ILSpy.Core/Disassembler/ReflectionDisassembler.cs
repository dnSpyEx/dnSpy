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
using System.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	public sealed class InstructionOperandConverter {
		readonly Dictionary<object, object> dict;
		readonly List<SourceLocal> sourceLocals;

		public InstructionOperandConverter() {
			dict = new Dictionary<object, object>();
			sourceLocals = new List<SourceLocal>();
		}

		public object Convert(object obj) {
			if (obj != null && dict.TryGetValue(obj, out var other))
				return other;
			return obj;
		}

		public void Clear() {
			dict.Clear();
			sourceLocals.Clear();
		}

		public SourceLocal[] GetSourceLocals() => sourceLocals.ToArray();

		public void Add(MethodDef method) {
			var body = method.Body;
			if (body == null)
				return;
			for (int i = 0; i < body.Variables.Count; i++) {
				var local = body.Variables[i];
				var sourceLocal = new SourceLocal(local, CreateLocalName(local), local.Type, SourceVariableFlags.None);
				sourceLocals.Add(sourceLocal);
				dict.Add(local, sourceLocal);
			}
		}

		static string CreateLocalName(Local local) {
			var name = local.Name;
			if (!string.IsNullOrEmpty(name))
				return name;
			return "V_" + local.Index.ToString();
		}
	}

	readonly struct BracePairHelper {
		readonly IDecompilerOutput output;
		readonly CodeBracesRangeFlags flags;
		readonly int leftStart, leftEnd;

		BracePairHelper(IDecompilerOutput output, int leftStart, int leftEnd, CodeBracesRangeFlags flags) {
			this.output = output;
			this.leftStart = leftStart;
			this.leftEnd = leftEnd;
			this.flags = flags;
		}

		public static BracePairHelper Create(IDecompilerOutput output, string s, CodeBracesRangeFlags flags) {
			int start = output.NextPosition;
			output.Write(s, BoxedTextColor.Punctuation);
			return new BracePairHelper(output, start, output.NextPosition, flags);
		}

		public void Write(string s) {
			int start = output.NextPosition;
			output.Write(s, BoxedTextColor.Punctuation);
			output.AddBracePair(new TextSpan(leftStart, leftEnd - leftStart), new TextSpan(start, output.NextPosition - start), flags);
		}
	}

	public class DisassemblerOptions
	{
		public DisassemblerOptions(int optionsVersion, CancellationToken cancellationToken, ModuleDef ownerModule)
		{
			this.OptionsVersion = optionsVersion;
			this.CancellationToken = cancellationToken;
			this.OwnerModule = ownerModule;
			this.MaxStringLength = int.MaxValue;
		}

		public readonly ModuleDef OwnerModule;

		public readonly CancellationToken CancellationToken;

		/// <summary>
		/// null if we shouldn't add opcode documentation. It returns null if no doc was found
		/// </summary>
		public Func<OpCode, string> GetOpCodeDocumentation;

		/// <summary>
		/// null if we shouldn't add XML doc comments.
		/// </summary>
		public Func<IMemberRef, IEnumerable<string>> GetXmlDocComments;

		/// <summary>
		/// Creates a <see cref="IInstructionBytesReader"/> instance
		/// </summary>
		public Func<MethodDef, IInstructionBytesReader> CreateInstructionBytesReader;

		/// <summary>
		/// Show tokens, RVAs, file offsets
		/// </summary>
		public bool ShowTokenAndRvaComments;

		/// <summary>
		/// Show IL instruction bytes
		/// </summary>
		public bool ShowILBytes;

		/// <summary>
		/// Sort members if true
		/// </summary>
		public bool SortMembers;

		/// <summary>
		/// Shows line numbers if a PDB file has been loaded
		/// </summary>
		public bool ShowPdbInfo;

		/// <summary>
		/// Max length of a string
		/// </summary>
		public int MaxStringLength;

		/// <summary>
		/// Use hexadecimal numbers
		/// </summary>
		public bool HexadecimalNumbers;

		/// <summary>
		/// Gets incremented when the options change
		/// </summary>
		public readonly int OptionsVersion;
	}

	/// <summary>
	/// Disassembles type and member definitions.
	/// </summary>
	public sealed class ReflectionDisassembler
	{
		readonly IDecompilerOutput output;
		readonly DisassemblerOptions options;
		readonly InstructionOperandConverter instructionOperandConverter;
		readonly NumberFormatter numberFormatter;
		readonly StringBuilder sb;
		bool isInType; // whether we are currently disassembling a whole type (-> defaultCollapsed for foldings)
		readonly MethodBodyDisassembler methodBodyDisassembler;

		const DecompilerReferenceFlags numberFlags = DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Hidden | DecompilerReferenceFlags.NoFollow;

		public ReflectionDisassembler(IDecompilerOutput output, bool detectControlStructure, DisassemblerOptions options)
		{
			if (output == null)
				throw new ArgumentNullException("output");
			this.output = output;
			this.options = options;
			sb = new StringBuilder();
			this.methodBodyDisassembler = new MethodBodyDisassembler(output, detectControlStructure, options, sb);
			this.instructionOperandConverter = new InstructionOperandConverter();
			numberFormatter = NumberFormatter.GetCSharpInstance(hex: options.HexadecimalNumbers, upper: true);
		}

		public ReflectionDisassembler(IDecompilerOutput output, bool detectControlStructure, DisassemblerOptions options, StringBuilder sb)
		{
			if (output == null)
				throw new ArgumentNullException("output");
			this.output = output;
			this.options = options;
			this.sb = sb;
			this.methodBodyDisassembler = new MethodBodyDisassembler(output, detectControlStructure, options, sb);
			this.instructionOperandConverter = new InstructionOperandConverter();
			numberFormatter = NumberFormatter.GetCSharpInstance(hex: options.HexadecimalNumbers, upper: true);
		}

		public ReflectionDisassembler(IDecompilerOutput output, DisassemblerOptions options, MethodBodyDisassembler methodBodyDisassembler, StringBuilder sb)
		{
			if (output == null)
				throw new ArgumentNullException("output");
			this.output = output;
			this.options = options;
			this.sb = sb;
			this.methodBodyDisassembler = methodBodyDisassembler;
			this.instructionOperandConverter = new InstructionOperandConverter();
			numberFormatter = NumberFormatter.GetCSharpInstance(hex: options.HexadecimalNumbers, upper: true);
		}

		#region Disassemble Method
		readonly EnumNameCollection<MethodAttributes> methodAttributeFlags = new EnumNameCollection<MethodAttributes>() {
			{ MethodAttributes.Final, "final" },
			{ MethodAttributes.HideBySig, "hidebysig" },
			{ MethodAttributes.SpecialName, "specialname" },
			{ MethodAttributes.PinvokeImpl, null }, // handled separately
			{ MethodAttributes.UnmanagedExport, "unmanagedexp" },
			{ MethodAttributes.RTSpecialName, "rtspecialname" },
			{ MethodAttributes.RequireSecObject, "reqsecobj" },
			{ MethodAttributes.NewSlot, "newslot" },
			{ MethodAttributes.CheckAccessOnOverride, "strict" },
			{ MethodAttributes.Abstract, "abstract" },
			{ MethodAttributes.Virtual, "virtual" },
			{ MethodAttributes.Static, "static" },
			{ MethodAttributes.HasSecurity, null }, // ?? also invisible in ILDasm
		};

		readonly EnumNameCollection<MethodAttributes> methodVisibility = new EnumNameCollection<MethodAttributes>() {
			{ MethodAttributes.Private, "private" },
			{ MethodAttributes.FamANDAssem, "famandassem" },
			{ MethodAttributes.Assembly, "assembly" },
			{ MethodAttributes.Family, "family" },
			{ MethodAttributes.FamORAssem, "famorassem" },
			{ MethodAttributes.Public, "public" },
		};

		readonly EnumNameCollection<CallingConvention> callingConvention = new EnumNameCollection<CallingConvention>() {
			{ CallingConvention.C, "unmanaged cdecl" },
			{ CallingConvention.StdCall, "unmanaged stdcall" },
			{ CallingConvention.ThisCall, "unmanaged thiscall" },
			{ CallingConvention.FastCall, "unmanaged fastcall" },
			{ CallingConvention.VarArg, "vararg" },
			{ CallingConvention.NativeVarArg, "nativevararg" },
			{ CallingConvention.Unmanaged, "unmanaged" },
			{ CallingConvention.Generic, null },
		};

		readonly EnumNameCollection<MethodImplAttributes> methodCodeType = new EnumNameCollection<MethodImplAttributes>() {
			{ MethodImplAttributes.IL, "cil" },
			{ MethodImplAttributes.Native, "native" },
			{ MethodImplAttributes.OPTIL, "optil" },
			{ MethodImplAttributes.Runtime, "runtime" },
		};

		readonly EnumNameCollection<MethodImplAttributes> methodImpl = new EnumNameCollection<MethodImplAttributes>() {
			{ MethodImplAttributes.Synchronized, "synchronized" },
			{ MethodImplAttributes.NoInlining, "noinlining" },
			{ MethodImplAttributes.NoOptimization, "nooptimization" },
			{ MethodImplAttributes.PreserveSig, "preservesig" },
			{ MethodImplAttributes.InternalCall, "internalcall" },
			{ MethodImplAttributes.ForwardRef, "forwardref" },
			{ MethodImplAttributes.AggressiveInlining, "aggressiveinlining" },
			{ MethodImplAttributes.AggressiveOptimization, "aggressiveoptimization" },
			{ MethodImplAttributes.SecurityMitigations, "securitymitigations" },
		};

		void WriteXmlDocComment(IMemberDef mr) {
			if (options.GetXmlDocComments == null)
				return;
			foreach (var line in options.GetXmlDocComments(mr)) {
				output.Write("///", BoxedTextColor.XmlDocCommentDelimiter);
				output.WriteXmlDoc(line);
				output.WriteLine();
			}
		}

		public void DisassembleMethod(MethodDef method, bool addLineSep = true)
		{
			// write method header
			WriteXmlDocComment(method);
			AddComment(method);
			int methodStartPosition = output.NextPosition;
			output.Write(".method", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			DisassembleMethodInternal(method, addLineSep, methodStartPosition);
		}

		void DisassembleMethodInternal(MethodDef method, bool addLineSep, int methodStartPosition)
		{
			//    .method public hidebysig  specialname
			//               instance default class [mscorlib]System.IO.TextWriter get_BaseWriter ()  cil managed
			//

			//emit flags
			WriteEnum(method.Attributes & MethodAttributes.MemberAccessMask, methodVisibility);
			WriteFlags(method.Attributes & ~MethodAttributes.MemberAccessMask, methodAttributeFlags);
			if (method.IsCompilerControlled) {
				output.Write("privatescope", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			}

			if (method.IsPinvokeImpl) {
				output.Write("pinvokeimpl", BoxedTextColor.Keyword);
				var bh2 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
				if (method.HasImplMap) {
					WriteImplMap(method.ImplMap, method.Name);
				}
				bh2.Write(")");
				output.Write(" ", BoxedTextColor.Text);
			}

			output.WriteLine();
			output.IncreaseIndent();
			if (method.ExplicitThis) {
				output.Write("instance", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("explicit", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			} else if (method.HasThis) {
				output.Write("instance", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			}

			//call convention
			WriteEnum(method.CallingConvention, callingConvention);

			//return type
			method.ReturnType.WriteTo(output, sb);
			output.Write(" ", BoxedTextColor.Text);
			if (method.Parameters.ReturnParameter.HasParamDef && method.Parameters.ReturnParameter.ParamDef.HasMarshalType) {
				WriteMarshalInfo(method.Parameters.ReturnParameter.ParamDef.MarshalType);
			}

			if (method.IsCompilerControlled) {
				output.Write(DisassemblerHelpers.Escape(method.Name + "$PST" + method.MDToken.ToInt32().ToString("X8")), method, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(method));
			} else {
				output.Write(DisassemblerHelpers.Escape(method.Name), method, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(method));
			}

			WriteTypeParameters(method);

			//( params )
			output.Write(" ", BoxedTextColor.Text);
			var bh3 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
			if (method.Parameters.GetNumberOfNormalParameters() > 0) {
				output.WriteLine();
				output.IncreaseIndent();
				WriteParameters(method.Parameters);
				output.DecreaseIndent();
			}
			bh3.Write(")");
			output.Write(" ", BoxedTextColor.Text);
			//cil managed
			WriteEnum(method.ImplAttributes & MethodImplAttributes.CodeTypeMask, methodCodeType);
			if ((method.ImplAttributes & MethodImplAttributes.ManagedMask) == MethodImplAttributes.Managed)
				output.Write("managed", BoxedTextColor.Keyword);
			else
				output.Write("unmanaged", BoxedTextColor.Keyword);
			output.Write(" ", BoxedTextColor.Text);
			WriteFlags(method.ImplAttributes & ~(MethodImplAttributes.CodeTypeMask | MethodImplAttributes.ManagedMask), methodImpl);

			output.DecreaseIndent();
			var bh1 = OpenBlock(flags: CodeBracesRangeFlags.MethodBraces);
			WriteAttributes(method.CustomAttributes);
			if (method.HasOverrides) {
				for (int i = 0; i < method.Overrides.Count; i++) {
					output.Write(".override", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("method", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					method.Overrides[i].MethodDeclaration.WriteMethodTo(output, sb);
					output.WriteLine();
				}
			}

			for (int i = 0; i < method.GenericParameters.Count; i++)
				WriteGenericParameterAttributes(method.GenericParameters[i]);

			WriteParameterAttributes(0, method.Parameters.ReturnParameter);
			for (int i = 0; i < method.Parameters.Count; i++) {
				var p = method.Parameters[i];
				if (p.IsHiddenThisParameter)
					continue;
				WriteParameterAttributes(p.MethodSigIndex + 1, p);
			}
			WriteSecurityDeclarations(method);

			MethodDebugInfoBuilder builder = null;
			if (method.HasBody) {
				instructionOperandConverter.Clear();
				instructionOperandConverter.Add(method);
				var sourceParams = method.Parameters.SkipNonNormal().Select(parameter => new SourceParameter(parameter, parameter.Name, parameter.Type, SourceVariableFlags.None)).ToArray();
				builder = new MethodDebugInfoBuilder(options.OptionsVersion, StateMachineKind.None, method, null, instructionOperandConverter.GetSourceLocals(), sourceParams, null) {
					StartPosition = methodStartPosition
				};
				methodBodyDisassembler.Disassemble(method, builder, instructionOperandConverter);
			}

			int methodEndPosition = CloseBlock(bh1, addLineSep, "end of method " + DisassemblerHelpers.Escape(method.DeclaringType.Name) + "::" + DisassemblerHelpers.Escape(method.Name));

			if (method.HasBody) {
				builder!.EndPosition = methodEndPosition;
				output.AddDebugInfo(builder.Create());
			}
		}

		#region Write Security Declarations
		void WriteSecurityDeclarations(IHasDeclSecurity secDeclProvider) {
			if (!secDeclProvider.HasDeclSecurities)
				return;

			for (int i = 0; i < secDeclProvider.DeclSecurities.Count; i++) {
				var secdecl = secDeclProvider.DeclSecurities[i];
				output.Write(".permissionset", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				switch (secdecl.Action) {
					case SecurityAction.Request:
						output.Write("request", BoxedTextColor.Keyword);
						break;
					case SecurityAction.Demand:
						output.Write("demand", BoxedTextColor.Keyword);
						break;
					case SecurityAction.Assert:
						output.Write("assert", BoxedTextColor.Keyword);
						break;
					case SecurityAction.Deny:
						output.Write("deny", BoxedTextColor.Keyword);
						break;
					case SecurityAction.PermitOnly:
						output.Write("permitonly", BoxedTextColor.Keyword);
						break;
					case SecurityAction.LinkDemand:
						output.Write("linkcheck", BoxedTextColor.Keyword);
						break;
					case SecurityAction.InheritDemand:
						output.Write("inheritcheck", BoxedTextColor.Keyword);
						break;
					case SecurityAction.RequestMinimum:
						output.Write("reqmin", BoxedTextColor.Keyword);
						break;
					case SecurityAction.RequestOptional:
						output.Write("reqopt", BoxedTextColor.Keyword);
						break;
					case SecurityAction.RequestRefuse:
						output.Write("reqrefuse", BoxedTextColor.Keyword);
						break;
					case SecurityAction.PreJitGrant:
						output.Write("prejitgrant", BoxedTextColor.Keyword);
						break;
					case SecurityAction.PreJitDeny:
						output.Write("prejitdeny", BoxedTextColor.Keyword);
						break;
					case SecurityAction.NonCasDemand:
						output.Write("noncasdemand", BoxedTextColor.Keyword);
						break;
					case SecurityAction.NonCasLinkDemand:
						output.Write("noncaslinkdemand", BoxedTextColor.Keyword);
						break;
					case SecurityAction.NonCasInheritance:
						output.Write("noncasinheritance", BoxedTextColor.Keyword);
						break;
					default:
						output.Write(secdecl.Action.ToString(), BoxedTextColor.Keyword);
						break;
				}

				var blob = secdecl.GetBlob();
				if (blob is not null && (char)blob[0] != '.') {
					output.WriteLine();
					output.IncreaseIndent();
					output.Write("bytearray", BoxedTextColor.Keyword);
					WriteBlob(blob);
					output.WriteLine();
					output.DecreaseIndent();
				}
				else {
					output.Write(" ", BoxedTextColor.Text);
					output.Write("=", BoxedTextColor.Operator);
					output.Write(" ", BoxedTextColor.Text);
					var bh1 = BracePairHelper.Create(output, "{", CodeBracesRangeFlags.OtherBlockBraces);
					output.WriteLine();
					output.IncreaseIndent();

					for (int j = 0; j < secdecl.SecurityAttributes.Count; j++) {
						SecurityAttribute sa = secdecl.SecurityAttributes[j];
						if (sa.AttributeType != null && sa.AttributeType.Scope == sa.AttributeType.Module) {
							output.Write("class", BoxedTextColor.Keyword);
							output.Write(" ", BoxedTextColor.Text);
							sb.Clear();
							output.Write(DisassemblerHelpers.Escape(FullNameFactory.AssemblyQualifiedName(sa.AttributeType, null, sb)), BoxedTextColor.Text);
						} else {
							sa.AttributeType.WriteTo(output, sb, ILNameSyntax.TypeName);
						}
						output.Write(" ", BoxedTextColor.Text);
						output.Write("=", BoxedTextColor.Operator);
						output.Write(" ", BoxedTextColor.Text);
						var bh2 = BracePairHelper.Create(output, "{", CodeBracesRangeFlags.OtherBlockBraces);
						if (sa.HasNamedArguments) {
							output.WriteLine();
							output.IncreaseIndent();

							var attrType = sa.AttributeType.ResolveTypeDef();
							foreach (var na in sa.Fields) {
								output.Write("field", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								WriteSecurityDeclarationArgument(attrType, na);
								output.WriteLine();
							}

							foreach (var na in sa.Properties) {
								output.Write("property", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								WriteSecurityDeclarationArgument(attrType, na);
								output.WriteLine();
							}

							output.DecreaseIndent();
						}
						bh2.Write("}");

						if (j + 1 < secdecl.SecurityAttributes.Count)
							output.Write(",", BoxedTextColor.Punctuation);
						output.WriteLine();
					}

					output.DecreaseIndent();
					bh1.Write("}");
					output.WriteLine();
				}
			}
		}

		void WriteSecurityDeclarationArgument(TypeDef attrType, CANamedArgument na)
		{
			object reference = null;
			if (attrType != null) {
				if (na.IsField)
					reference = attrType.FindField(na.Name, new FieldSig(na.Type));
				else
					reference = attrType.FindProperty(na.Name, PropertySig.CreateInstance(na.Type));
			}

			TypeSig type = na.Argument.Type;
			if (type != null && (type.ElementType == ElementType.Class || type.ElementType == ElementType.ValueType)) {
				output.Write("enum", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				if (type.Scope != type.Module) {
					output.Write("class", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					sb.Clear();
					output.Write(DisassemblerHelpers.Escape(FullNameFactory.AssemblyQualifiedName(type, null, sb)), BoxedTextColor.Text);
				} else {
					type.WriteTo(output, sb, ILNameSyntax.TypeName);
				}
			} else {
				type.WriteTo(output, sb);
			}
			output.Write(" ", BoxedTextColor.Text);
			output.Write(DisassemblerHelpers.Escape(na.Name), reference, DecompilerReferenceFlags.None, na.IsField ? BoxedTextColor.InstanceField : BoxedTextColor.InstanceProperty);
			output.Write(" ", BoxedTextColor.Text);
			output.Write("=", BoxedTextColor.Operator);
			output.Write(" ", BoxedTextColor.Text);
			if (na.Argument.Value is UTF8String) {
				// secdecls use special syntax for strings
				output.Write("string", BoxedTextColor.Keyword);
				var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
				output.Write(string.Format("'{0}'", ILStringEscaper.ConvertStringMaxLength((UTF8String)na.Argument.Value, options.MaxStringLength).Replace("'", "\'")), BoxedTextColor.String);
				bh1.Write(")");
			} else {
				WriteConstant(na.Argument.Value);
			}
		}

		#endregion

		#region WriteMarshalInfo
		void WriteMarshalInfo(MarshalType marshalInfo)
		{
			output.Write("marshal", BoxedTextColor.Keyword);
			var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
			if (marshalInfo != null)
				WriteNativeType(marshalInfo.NativeType, marshalInfo);
			bh1.Write(")");
			output.Write(" ", BoxedTextColor.Text);
		}

		void WriteNativeType(NativeType nativeType, MarshalType marshalInfo = null)
		{
			switch (nativeType) {
				case NativeType.NotInitialized:
					break;
				case NativeType.Boolean:
					output.Write("bool", BoxedTextColor.Keyword);
					break;
				case NativeType.I1:
					output.Write("int8", BoxedTextColor.Keyword);
					break;
				case NativeType.U1:
					output.Write("unsigned", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("int8", BoxedTextColor.Keyword);
					break;
				case NativeType.I2:
					output.Write("int16", BoxedTextColor.Keyword);
					break;
				case NativeType.U2:
					output.Write("unsigned", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("int16", BoxedTextColor.Keyword);
					break;
				case NativeType.I4:
					output.Write("int32", BoxedTextColor.Keyword);
					break;
				case NativeType.U4:
					output.Write("unsigned", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("int32", BoxedTextColor.Keyword);
					break;
				case NativeType.I8:
					output.Write("int64", BoxedTextColor.Keyword);
					break;
				case NativeType.U8:
					output.Write("unsigned", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("int64", BoxedTextColor.Keyword);
					break;
				case NativeType.R4:
					output.Write("float32", BoxedTextColor.Keyword);
					break;
				case NativeType.R8:
					output.Write("float64", BoxedTextColor.Keyword);
					break;
				case NativeType.LPStr:
					output.Write("lpstr", BoxedTextColor.Keyword);
					break;
				case NativeType.Int:
					output.Write("int", BoxedTextColor.Keyword);
					break;
				case NativeType.UInt:
					output.Write("unsigned", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("int", BoxedTextColor.Keyword);
					break;
				case NativeType.Func:
					output.Write("method", BoxedTextColor.Keyword);
					break;
				case NativeType.Array:
					ArrayMarshalType ami = marshalInfo as ArrayMarshalType;
					if (ami == null)
						goto default;
					if (ami.ElementType != NativeType.Max)
						WriteNativeType(ami.ElementType);
					var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
					if (ami.Size >= 0) {
						output.Write(numberFormatter.Format(ami.Size), ami.Size, numberFlags, BoxedTextColor.Number);
					}
					if (ami.Flags != 0 && ami.ParamNumber >= 0) {
						output.Write(" ", BoxedTextColor.Text);
						output.Write("+", BoxedTextColor.Operator);
						output.Write(" ", BoxedTextColor.Text);
						output.Write(numberFormatter.Format(ami.ParamNumber), ami.ParamNumber, numberFlags, BoxedTextColor.Number);
					}
					bh1.Write("]");
					break;
				case NativeType.Currency:
					output.Write("currency", BoxedTextColor.Keyword);
					break;
				case NativeType.BStr:
					output.Write("bstr", BoxedTextColor.Keyword);
					break;
				case NativeType.LPWStr:
					output.Write("lpwstr", BoxedTextColor.Keyword);
					break;
				case NativeType.LPTStr:
					output.Write("lptstr", BoxedTextColor.Keyword);
					break;
				case NativeType.FixedSysString:
					var fsmi = marshalInfo as FixedSysStringMarshalType;
					output.Write("fixed", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("sysstring", BoxedTextColor.Keyword);
					if (fsmi != null && fsmi.IsSizeValid) {
						var bh2 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
						output.Write(numberFormatter.Format(fsmi.Size), fsmi.Size, numberFlags, BoxedTextColor.Number);
						bh2.Write("]");
					}
					break;
				case NativeType.IUnknown:
				case NativeType.IDispatch:
				case NativeType.IntF:
					if (nativeType == NativeType.IUnknown)
						output.Write("iunknown", BoxedTextColor.Keyword);
					else if (nativeType == NativeType.IDispatch)
						output.Write("idispatch", BoxedTextColor.Keyword);
					else
						output.Write("interface", BoxedTextColor.Keyword);
					var imti = marshalInfo as InterfaceMarshalType;
					if (imti != null && imti.IsIidParamIndexValid) {
						var bh2 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
						output.Write("iidparam", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
						output.Write("=", BoxedTextColor.Operator);
						output.Write(" ", BoxedTextColor.Text);
						output.Write(numberFormatter.Format(imti.IidParamIndex), imti.IsIidParamIndexValid, numberFlags, BoxedTextColor.Number);
						bh2.Write(")");
					}
					break;
				case NativeType.Struct:
					output.Write("struct", BoxedTextColor.Keyword);
					break;
				case NativeType.SafeArray:
					output.Write("safearray", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					SafeArrayMarshalType sami = marshalInfo as SafeArrayMarshalType;
					if (sami != null && sami.IsVariantTypeValid) {
						switch (sami.VariantType & VariantType.TypeMask) {
							case VariantType.None:
								break;
							case VariantType.Null:
								output.Write("null", BoxedTextColor.Keyword);
								break;
							case VariantType.I2:
								output.Write("int16", BoxedTextColor.Keyword);
								break;
							case VariantType.I4:
								output.Write("int32", BoxedTextColor.Keyword);
								break;
							case VariantType.R4:
								output.Write("float32", BoxedTextColor.Keyword);
								break;
							case VariantType.R8:
								output.Write("float64", BoxedTextColor.Keyword);
								break;
							case VariantType.CY:
								output.Write("currency", BoxedTextColor.Keyword);
								break;
							case VariantType.Date:
								output.Write("date", BoxedTextColor.Keyword);
								break;
							case VariantType.BStr:
								output.Write("bstr", BoxedTextColor.Keyword);
								break;
							case VariantType.Dispatch:
								output.Write("idispatch", BoxedTextColor.Keyword);
								break;
							case VariantType.Error:
								output.Write("error", BoxedTextColor.Keyword);
								break;
							case VariantType.Bool:
								output.Write("bool", BoxedTextColor.Keyword);
								break;
							case VariantType.Variant:
								output.Write("variant", BoxedTextColor.Keyword);
								break;
							case VariantType.Unknown:
								output.Write("iunknown", BoxedTextColor.Keyword);
								break;
							case VariantType.Decimal:
								output.Write("decimal", BoxedTextColor.Keyword);
								break;
							case VariantType.I1:
								output.Write("int8", BoxedTextColor.Keyword);
								break;
							case VariantType.UI1:
								output.Write("unsigned", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								output.Write("int8", BoxedTextColor.Keyword);
								break;
							case VariantType.UI2:
								output.Write("unsigned", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								output.Write("int16", BoxedTextColor.Keyword);
								break;
							case VariantType.UI4:
								output.Write("unsigned", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								output.Write("int32", BoxedTextColor.Keyword);
								break;
							case VariantType.I8:
								output.Write("int64", BoxedTextColor.Keyword);
								break;
							case VariantType.UI8:
								output.Write("unsigned", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								output.Write("int64", BoxedTextColor.Keyword);
								break;
							case VariantType.Int:
								output.Write("int", BoxedTextColor.Keyword);
								break;
							case VariantType.UInt:
								output.Write("unsigned", BoxedTextColor.Keyword);
								output.Write(" ", BoxedTextColor.Text);
								output.Write("int", BoxedTextColor.Keyword);
								break;
							case VariantType.Void:
								output.Write("void", BoxedTextColor.Keyword);
								break;
							case VariantType.HResult:
								output.Write("hresult", BoxedTextColor.Keyword);
								break;
							case VariantType.Ptr:
								output.Write("*", BoxedTextColor.Operator);
								break;
							case VariantType.SafeArray:
								output.Write("safearray", BoxedTextColor.Keyword);
								break;
							case VariantType.CArray:
								output.Write("carray", BoxedTextColor.Keyword);
								break;
							case VariantType.UserDefined:
								output.Write("userdefined", BoxedTextColor.Keyword);
								break;
							case VariantType.LPStr:
								output.Write("lpstr", BoxedTextColor.Keyword);
								break;
							case VariantType.LPWStr:
								output.Write("lpwstr", BoxedTextColor.Keyword);
								break;
							case VariantType.Record:
								output.Write("record", BoxedTextColor.Keyword);
								break;
							case VariantType.FileTime:
								output.Write("filetime", BoxedTextColor.Keyword);
								break;
							case VariantType.Blob:
								output.Write("blob", BoxedTextColor.Keyword);
								break;
							case VariantType.Stream:
								output.Write("stream", BoxedTextColor.Keyword);
								break;
							case VariantType.Storage:
								output.Write("storage", BoxedTextColor.Keyword);
								break;
							case VariantType.StreamedObject:
								output.Write("streamed_object", BoxedTextColor.Keyword);
								break;
							case VariantType.StoredObject:
								output.Write("stored_object", BoxedTextColor.Keyword);
								break;
							case VariantType.BlobObject:
								output.Write("blob_object", BoxedTextColor.Keyword);
								break;
							case VariantType.CF:
								output.Write("cf", BoxedTextColor.Keyword);
								break;
							case VariantType.CLSID:
								output.Write("clsid", BoxedTextColor.Keyword);
								break;
							case VariantType.IntPtr:
							case VariantType.UIntPtr:
							case VariantType.VersionedStream:
							case VariantType.BStrBlob:
							default:
								output.Write((sami.VariantType & VariantType.TypeMask).ToString(), BoxedTextColor.Keyword);
								break;
						}
						if ((sami.VariantType & VariantType.ByRef) != 0)
							output.Write("&", BoxedTextColor.Operator);
						if ((sami.VariantType & VariantType.Array) != 0) {
							var bh = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
							bh.Write("]");
						}
						if ((sami.VariantType & VariantType.Vector) != 0) {
							output.Write(" ", BoxedTextColor.Text);
							output.Write("vector", BoxedTextColor.Keyword);
						}
						if (sami.IsUserDefinedSubTypeValid) {
							output.Write(",", BoxedTextColor.Punctuation);
							output.Write(" ", BoxedTextColor.Text);
							sb.Clear();
							output.Write("\"" + ILStringEscaper.ConvertStringMaxLength(FullNameFactory.FullName(sami.UserDefinedSubType, false, null, sb), options.MaxStringLength) + "\"", BoxedTextColor.String);
						}
					}
					break;
				case NativeType.FixedArray:
					output.Write("fixed", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("array", BoxedTextColor.Keyword);
					FixedArrayMarshalType fami = marshalInfo as FixedArrayMarshalType;
					if (fami != null) {
						if (fami.IsSizeValid) {
							var bh2 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
							output.Write(numberFormatter.Format(fami.Size), fami.Size, numberFlags, BoxedTextColor.Number);
							bh2.Write("]");
						}
						if (fami.IsElementTypeValid) {
							output.Write(" ", BoxedTextColor.Text);
							WriteNativeType(fami.ElementType);
						}
					}
					break;
				case NativeType.ByValStr:
					output.Write("byvalstr", BoxedTextColor.Keyword);
					break;
				case NativeType.ANSIBStr:
					output.Write("ansi", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("bstr", BoxedTextColor.Keyword);
					break;
				case NativeType.TBStr:
					output.Write("tbstr", BoxedTextColor.Keyword);
					break;
				case NativeType.VariantBool:
					output.Write("variant", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("bool", BoxedTextColor.Keyword);
					break;
				case NativeType.ASAny:
					output.Write("as", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("any", BoxedTextColor.Keyword);
					break;
				case NativeType.LPStruct:
					output.Write("lpstruct", BoxedTextColor.Keyword);
					break;
				case NativeType.CustomMarshaler:
					CustomMarshalType cmi = marshalInfo as CustomMarshalType;
					if (cmi == null)
						goto default;
					output.Write("custom", BoxedTextColor.Keyword);
					var bh3 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
					string customMarshalerFullName;
					if (cmi.CustomMarshaler is null)
						customMarshalerFullName = string.Empty;
					else {
						sb.Clear();
						customMarshalerFullName = FullNameFactory.FullName(cmi.CustomMarshaler, false, null, sb);
					}
					output.Write(string.Format("\"{0}\"", ILStringEscaper.ConvertStringMaxLength(customMarshalerFullName, options.MaxStringLength)), BoxedTextColor.String);
					output.Write(",", BoxedTextColor.Punctuation);
					output.Write(" ", BoxedTextColor.Text);
					output.Write(string.Format("\"{0}\"", ILStringEscaper.ConvertStringMaxLength(cmi.Cookie, options.MaxStringLength)), BoxedTextColor.String);
					if (!UTF8String.IsNullOrEmpty(cmi.Guid) || !UTF8String.IsNullOrEmpty(cmi.NativeTypeName)) {
						output.Write(",", BoxedTextColor.Punctuation);
						output.Write(" ", BoxedTextColor.Text);
						output.Write(string.Format("\"{0}\"", ILStringEscaper.ConvertStringMaxLength(cmi.Guid, options.MaxStringLength)), BoxedTextColor.String);
						output.Write(",", BoxedTextColor.Punctuation);
						output.Write(" ", BoxedTextColor.Text);
						output.Write(string.Format("\"{0}\"", ILStringEscaper.ConvertStringMaxLength(cmi.NativeTypeName, options.MaxStringLength)), BoxedTextColor.String);
					}
					bh3.Write(")");
					break;
				case NativeType.Error:
					output.Write("error", BoxedTextColor.Keyword);
					break;
				case NativeType.Void:
					output.Write("void", BoxedTextColor.Keyword);
					break;
				case NativeType.SysChar:
					output.Write("syschar", BoxedTextColor.Keyword);
					break;
				case NativeType.Variant:
					output.Write("variant", BoxedTextColor.Keyword);
					break;
				case NativeType.Decimal:
					output.Write("decimal", BoxedTextColor.Keyword);
					break;
				case NativeType.Date:
					output.Write("date", BoxedTextColor.Keyword);
					break;
				case NativeType.ObjectRef:
					output.Write("objectref", BoxedTextColor.Keyword);
					break;
				case NativeType.NestedStruct:
					output.Write("nested", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("struct", BoxedTextColor.Keyword);
					break;
				case NativeType.Ptr:
				case NativeType.IInspectable:
				case NativeType.HString:
				case NativeType.LPUTF8Str:
				default:
					output.Write(nativeType.ToString(), BoxedTextColor.Keyword);
					break;
			}
		}
		#endregion

		void WriteParameters(IList<Parameter> parameters)
		{
			for (int i = 0; i < parameters.Count; i++) {
				var p = parameters[i];
				if (p.IsHiddenThisParameter)
					continue;
				var paramDef = p.ParamDef;
				if (paramDef != null) {
					if (paramDef.IsIn) {
						var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
						output.Write("in", BoxedTextColor.Keyword);
						bh1.Write("]");
						output.Write(" ", BoxedTextColor.Text);
					}
					if (paramDef.IsOut) {
						var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
						output.Write("out", BoxedTextColor.Keyword);
						bh1.Write("]");
						output.Write(" ", BoxedTextColor.Text);
					}
					if (paramDef.IsOptional) {
						var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
						output.Write("opt", BoxedTextColor.Keyword);
						bh1.Write("]");
						output.Write(" ", BoxedTextColor.Text);
					}
				}
				p.Type.WriteTo(output, sb);
				output.Write(" ", BoxedTextColor.Text);
				if (paramDef != null && paramDef.MarshalType != null) {
					WriteMarshalInfo(paramDef.MarshalType);
				}
				output.Write(DisassemblerHelpers.Escape(p.Name), p, DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition, BoxedTextColor.Parameter);
				if (i < parameters.Count - 1)
					output.Write(",", BoxedTextColor.Punctuation);
				output.WriteLine();
			}
		}

		bool HasParameterAttributes(Parameter p)
		{
			return p.ParamDef != null && (p.ParamDef.HasConstant || p.ParamDef.CustomAttributes.Count > 0);
		}

		void WriteGenericParameterAttributes(GenericParam p) {
			if (p.HasCustomAttributes) {
				output.Write(".param", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("type", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(DisassemblerHelpers.Escape(p.Name), CSharpMetadataTextColorProvider.Instance.GetColor(p));
				output.WriteLine();
				output.IncreaseIndent();
				WriteAttributes(p.CustomAttributes);
				output.DecreaseIndent();
			}
			for (int i = 0; i < p.GenericParamConstraints.Count; i++) {
				var constraint = p.GenericParamConstraints[i];
				if (constraint.HasCustomAttributes) {
					output.Write(".param", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("constraint", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					output.Write(DisassemblerHelpers.Escape(p.Name), CSharpMetadataTextColorProvider.Instance.GetColor(p));
					output.Write(",", BoxedTextColor.Punctuation);
					output.Write(" ", BoxedTextColor.Text);
					constraint.Constraint.WriteTo(output, sb, ILNameSyntax.TypeName);
					output.WriteLine();
					output.IncreaseIndent();
					WriteAttributes(constraint.CustomAttributes);
					output.DecreaseIndent();
				}
			}
		}

		void WriteParameterAttributes(int index, Parameter p)
		{
			if (!HasParameterAttributes(p))
				return;
			output.Write(".param", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
			output.Write(numberFormatter.Format(index), index, numberFlags, BoxedTextColor.Number);
			bh1.Write("]");
			if (p.HasParamDef && p.ParamDef.HasConstant) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("=", BoxedTextColor.Operator);
				output.Write(" ", BoxedTextColor.Text);
				WriteConstant(p.ParamDef.Constant.Value);
			}
			output.WriteLine();
			output.IncreaseIndent();
			if (p.HasParamDef)
				WriteAttributes(p.ParamDef.CustomAttributes);
			output.DecreaseIndent();
		}

		void WriteConstant(object constant)
		{
			if (constant == null) {
				output.Write("nullref", BoxedTextColor.Keyword);
			} else {
				string typeName = DisassemblerHelpers.PrimitiveTypeName(constant.GetType().FullName, options.OwnerModule, out var typeSig);
				if (typeName != null && typeName != "string") {
					DisassemblerHelpers.WriteKeyword(output, typeName, typeSig.ToTypeDefOrRef());
					var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
					if (constant is float cf && (float.IsNaN(cf) || float.IsInfinity(cf))) {
						uint asUint32 = BitConverter.ToUInt32(BitConverter.GetBytes(cf), 0);
						output.Write(numberFormatter.Format(asUint32), asUint32, numberFlags, BoxedTextColor.Number);
					}
					else if (constant is double cd && (double.IsNaN(cd) || double.IsInfinity(cd))) {
						ulong asUlong = (ulong)BitConverter.DoubleToInt64Bits(cd);
						output.Write(numberFormatter.Format(asUlong), asUlong, numberFlags, BoxedTextColor.Number);
					}
					else {
						DisassemblerHelpers.WriteOperand(output, constant, options.MaxStringLength, numberFormatter, sb);
					}
					bh1.Write(")");
				} else {
					DisassemblerHelpers.WriteOperand(output, constant, options.MaxStringLength, numberFormatter, sb);
				}
			}
		}
		#endregion

		#region Disassemble Field
		readonly EnumNameCollection<FieldAttributes> fieldVisibility = new EnumNameCollection<FieldAttributes>() {
			{ FieldAttributes.Private, "private" },
			{ FieldAttributes.FamANDAssem, "famandassem" },
			{ FieldAttributes.Assembly, "assembly" },
			{ FieldAttributes.Family, "family" },
			{ FieldAttributes.FamORAssem, "famorassem" },
			{ FieldAttributes.Public, "public" },
		};

		readonly EnumNameCollection<FieldAttributes> fieldAttributes = new EnumNameCollection<FieldAttributes>() {
			{ FieldAttributes.Static, "static" },
			{ FieldAttributes.Literal, "literal" },
			{ FieldAttributes.InitOnly, "initonly" },
			{ FieldAttributes.SpecialName, "specialname" },
			{ FieldAttributes.RTSpecialName, "rtspecialname" },
			{ FieldAttributes.NotSerialized, "notserialized" },
		};

		public void DisassembleField(FieldDef field, bool addLineSep = false)
		{
			WriteXmlDocComment(field);
			AddComment(field);
			output.Write(".field", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			if (field.HasLayoutInfo && field.FieldOffset.HasValue) {
				var bh1 = BracePairHelper.Create(output, "[", CodeBracesRangeFlags.SquareBrackets);
				output.Write(numberFormatter.Format(field.FieldOffset.Value), field.FieldOffset.Value, numberFlags, BoxedTextColor.Number);
				bh1.Write("]");
				output.Write(" ", BoxedTextColor.Text);
			}
			WriteEnum(field.Attributes & FieldAttributes.FieldAccessMask, fieldVisibility);
			const FieldAttributes hasXAttributes = FieldAttributes.HasDefault | FieldAttributes.HasFieldMarshal | FieldAttributes.HasFieldRVA;
			WriteFlags(field.Attributes & ~(FieldAttributes.FieldAccessMask | hasXAttributes), fieldAttributes);

			if (field.IsPinvokeImpl) {
				output.Write("pinvokeimpl", BoxedTextColor.Keyword);
				var bh2 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
				if (field.HasImplMap) {
					WriteImplMap(field.ImplMap, field.Name);
				}
				bh2.Write(")");
				output.Write(" ", BoxedTextColor.Text);
			}

			if (field.HasMarshalType) {
				WriteMarshalInfo(field.MarshalType);
			}

			field.FieldType.WriteTo(output, sb);
			output.Write(" ", BoxedTextColor.Text);

			if (field.IsPrivateScope) {
				output.Write(DisassemblerHelpers.Escape(field.Name + "$PST" + field.MDToken.ToInt32().ToString("X8")), field, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(field));
			} else {
				output.Write(DisassemblerHelpers.Escape(field.Name), field, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(field));
			}

			char sectionPrefix = 'D';
			if (field.HasFieldRVA) {
				sectionPrefix = GetRVASectionPrefix(field.Module, field.RVA);

				output.Write(" ", BoxedTextColor.Text);
				output.Write("at", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(string.Format("{0}_{1:X8}", sectionPrefix, (uint)field.RVA), Tuple.Create(field, field.RVA), DecompilerReferenceFlags.None, BoxedTextColor.Label);
			}
			if (field.HasConstant) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("=", BoxedTextColor.Operator);
				output.Write(" ", BoxedTextColor.Text);
				WriteConstant(field.Constant.Value);
			}
			if (addLineSep)
				output.AddLineSeparator(output.NextPosition);
			output.WriteLine();
			WriteAttributes(field.CustomAttributes);

			if (field.HasFieldRVA) {
				var sectionHeader = field.Module.GetContainingSection(field.RVA);
				if (sectionHeader is null) {
					output.WriteLine($"// RVA {(uint)field.RVA:X8} invalid (not in any section)", BoxedTextColor.Comment);
				}
				else if (field.InitialValue is null) {
					output.Write("// .data ", BoxedTextColor.Comment);
					output.Write(string.Format("{0}_{1:X8}", sectionPrefix, (uint)field.RVA), Tuple.Create(field, field.RVA), DecompilerReferenceFlags.Definition, BoxedTextColor.Comment);
					output.WriteLine(" = null", BoxedTextColor.Comment);
				}
				else if (field.InitialValue.Length > 0) {
					output.Write(".data", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					if (sectionHeader.DisplayName == ".text") {
						output.Write("cil", BoxedTextColor.Keyword);
					} else if (sectionHeader.DisplayName == ".tls") {
						output.Write("tls", BoxedTextColor.Keyword);
					} else if (sectionHeader.DisplayName != ".data") {
						output.Write($"/* {sectionHeader.DisplayName} */", BoxedTextColor.Comment);
					}
					output.Write(" ", BoxedTextColor.Text);
					output.Write(string.Format("{0}_{1:X8}", sectionPrefix, (uint)field.RVA), Tuple.Create(field, field.RVA), DecompilerReferenceFlags.Definition | DecompilerReferenceFlags.IsWrite, BoxedTextColor.Label);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("=", BoxedTextColor.Operator);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("bytearray", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					WriteBlob(field.InitialValue);
					output.WriteLine();
				}
			}
		}

		static char GetRVASectionPrefix(ModuleDef moduleDef, RVA rva) {
			var sectionHeader = moduleDef.GetContainingSection(rva);
			if (sectionHeader is null)
				return 'D';
			switch (sectionHeader.DisplayName) {
			case ".tls":
				return 'T';
			case ".text":
				return 'I';
			default:
				return 'D';
			}
		}
		#endregion

		#region Disassemble Property
		readonly EnumNameCollection<PropertyAttributes> propertyAttributes = new EnumNameCollection<PropertyAttributes>() {
			{ PropertyAttributes.SpecialName, "specialname" },
			{ PropertyAttributes.RTSpecialName, "rtspecialname" },
			{ PropertyAttributes.HasDefault, null },
		};

		public void DisassembleProperty(PropertyDef property, bool full = true, bool addLineSep = true)
		{
			WriteXmlDocComment(property);
			AddComment(property);
			output.Write(".property", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			WriteFlags(property.Attributes, propertyAttributes);
			if (property.PropertySig != null && property.PropertySig.HasThis) {
				output.Write("instance", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			}
			property.PropertySig.GetRetType().WriteTo(output, sb);
			output.Write(" ", BoxedTextColor.Text);
			output.Write(DisassemblerHelpers.Escape(property.Name), property, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(property));

			var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
			var parameters = new List<Parameter>(property.GetParameters());
			if (parameters.GetNumberOfNormalParameters() > 0) {
				output.WriteLine();
				output.IncreaseIndent();
				WriteParameters(parameters);
				output.DecreaseIndent();
			}
			bh1.Write(")");

			if (property.HasConstant) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("=", BoxedTextColor.Operator);
				output.Write(" ", BoxedTextColor.Text);
				WriteConstant(property.Constant.Value);
			}

			if (full) {
				var bh2 = OpenBlock(CodeBracesRangeFlags.PropertyBraces);
				WriteAttributes(property.CustomAttributes);

				for (int i = 0; i < property.GetMethods.Count; i++)
					WriteNestedMethod(".get", property.GetMethods[i]);
				for (int i = 0; i < property.SetMethods.Count; i++)
					WriteNestedMethod(".set", property.SetMethods[i]);
				for (int i = 0; i < property.OtherMethods.Count; i++)
					WriteNestedMethod(".other", property.OtherMethods[i]);
				CloseBlock(bh2, addLineSep);
			}
			else {
				output.Write(" ", BoxedTextColor.Text);
				var bh2 = BracePairHelper.Create(output, "{", CodeBracesRangeFlags.PropertyBraces);

				if (property.GetMethods.Count > 0) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write(".get", BoxedTextColor.Keyword);
					output.Write(";", BoxedTextColor.Punctuation);
				}

				if (property.SetMethods.Count > 0) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write(".set", BoxedTextColor.Keyword);
					output.Write(";", BoxedTextColor.Punctuation);
				}

				output.Write(" ", BoxedTextColor.Text);
				bh2.Write("}");
			}
		}

		void WriteNestedMethod(string keyword, MethodDef method)
		{
			if (method == null)
				return;

			AddComment(method);
			output.Write(keyword, BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			method.WriteMethodTo(output, sb);
			output.WriteLine();
		}
		#endregion

		#region Disassemble Event
		readonly EnumNameCollection<EventAttributes> eventAttributes = new EnumNameCollection<EventAttributes>() {
			{ EventAttributes.SpecialName, "specialname" },
			{ EventAttributes.RTSpecialName, "rtspecialname" },
		};

		public void DisassembleEvent(EventDef ev, bool full = true, bool addLineSep = true)
		{
			WriteXmlDocComment(ev);
			AddComment(ev);
			output.Write(".event", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			WriteFlags(ev.Attributes, eventAttributes);
			ev.EventType.WriteTo(output, sb, ILNameSyntax.TypeName);
			output.Write(" ", BoxedTextColor.Text);
			output.Write(DisassemblerHelpers.Escape(ev.Name), ev, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(ev));

			if (full) {
				var bh1 = OpenBlock(CodeBracesRangeFlags.EventBraces);
				WriteAttributes(ev.CustomAttributes);
				WriteNestedMethod(".addon", ev.AddMethod);
				WriteNestedMethod(".removeon", ev.RemoveMethod);
				WriteNestedMethod(".fire", ev.InvokeMethod);
				for (int i = 0; i < ev.OtherMethods.Count; i++)
					WriteNestedMethod(".other", ev.OtherMethods[i]);
				CloseBlock(bh1, addLineSep);
			}
			else {
				output.Write(" ", BoxedTextColor.Text);
				var bh1 = BracePairHelper.Create(output, "{", CodeBracesRangeFlags.EventBraces);

				if (ev.AddMethod != null) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write(".addon", BoxedTextColor.Keyword);
					output.Write(";", BoxedTextColor.Punctuation);
				}

				if (ev.RemoveMethod != null) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write(".removeon", BoxedTextColor.Keyword);
					output.Write(";", BoxedTextColor.Punctuation);
				}

				if (ev.InvokeMethod != null) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write(".fire", BoxedTextColor.Keyword);
					output.Write(";", BoxedTextColor.Punctuation);
				}

				output.Write(" ", BoxedTextColor.Text);
				bh1.Write("}");
			}
		}
		#endregion

		#region Disassemble Type
		readonly EnumNameCollection<TypeAttributes> typeVisibility = new EnumNameCollection<TypeAttributes>() {
			{ TypeAttributes.Public, "public" },
			{ TypeAttributes.NotPublic, "private" },
			{ TypeAttributes.NestedPublic, "nested public" },
			{ TypeAttributes.NestedPrivate, "nested private" },
			{ TypeAttributes.NestedAssembly, "nested assembly" },
			{ TypeAttributes.NestedFamily, "nested family" },
			{ TypeAttributes.NestedFamANDAssem, "nested famandassem" },
			{ TypeAttributes.NestedFamORAssem, "nested famorassem" },
		};

		readonly EnumNameCollection<TypeAttributes> typeLayout = new EnumNameCollection<TypeAttributes>() {
			{ TypeAttributes.AutoLayout, "auto" },
			{ TypeAttributes.SequentialLayout, "sequential" },
			{ TypeAttributes.ExplicitLayout, "explicit" },
		};

		readonly EnumNameCollection<TypeAttributes> typeStringFormat = new EnumNameCollection<TypeAttributes>() {
			{ TypeAttributes.AutoClass, "autochar" },
			{ TypeAttributes.AnsiClass, "ansi" },
			{ TypeAttributes.UnicodeClass, "unicode" },
		};

		readonly EnumNameCollection<TypeAttributes> typeAttributes = new EnumNameCollection<TypeAttributes>() {
			{ TypeAttributes.Abstract, "abstract" },
			{ TypeAttributes.Sealed, "sealed" },
			{ TypeAttributes.SpecialName, "specialname" },
			{ TypeAttributes.Import, "import" },
			{ TypeAttributes.Serializable, "serializable" },
			{ TypeAttributes.WindowsRuntime, "windowsruntime" },
			{ TypeAttributes.BeforeFieldInit, "beforefieldinit" },
			{ TypeAttributes.RTSpecialName, "rtspecialname" },
			{ TypeAttributes.HasSecurity, null },
		};

		void AddTokenComment(IMDTokenProvider member)
		{
			if (!options.ShowTokenAndRvaComments)
				return;

			StartComment();
			WriteToken(member);
			output.WriteLine();
		}

		void StartComment()
		{
			output.Write("//", BoxedTextColor.Comment);
		}

		void WriteToken(IMDTokenProvider member)
		{
			output.Write(" Token: ", BoxedTextColor.Comment);
			output.Write(string.Format("0x{0:X8}", member.MDToken.Raw), options.OwnerModule == null ? null : new TokenReference(options.OwnerModule, member.MDToken.Raw), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			output.Write(" RID: ", BoxedTextColor.Comment);
			output.Write(string.Format("{0}", member.MDToken.Rid), BoxedTextColor.Comment);
		}

		void WriteRVA(IMemberDef member)
		{
			uint rva;
			long fileOffset;
			member.GetRVA(out rva, out fileOffset);
			if (rva == 0)
				return;

			var mod = member.Module;
			var filename = mod == null ? null : mod.Location;
			output.Write(" RVA: ", BoxedTextColor.Comment);
			output.Write(string.Format("0x{0:X8}", rva), new AddressReference(filename, true, rva, 0), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			output.Write(" File Offset: ", BoxedTextColor.Comment);
			output.Write(string.Format("0x{0:X8}", fileOffset), new AddressReference(filename, false, (ulong)fileOffset, 0), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
		}

		void AddComment(IMemberDef member)
		{
			if (!options.ShowTokenAndRvaComments)
				return;

			StartComment();
			WriteToken(member);
			WriteRVA(member);
			output.WriteLine();
		}

		void WriteTypeName(TypeDef type) {
			var ns = type.Namespace ?? string.Empty;
			if (ns != string.Empty) {
				DisassemblerHelpers.WriteNamespace(output, ns, type.DefinitionAssembly, sb);
				output.Write(".", BoxedTextColor.Operator);
			}
			output.Write(DisassemblerHelpers.Escape(type.Name.String), type, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(type));
		}

		public void DisassembleType(TypeDef type, bool addLineSep = true)
		{
			// start writing IL
			WriteXmlDocComment(type);
			AddComment(type);
			output.Write(".class", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);

			if ((type.Attributes & TypeAttributes.ClassSemanticMask) == TypeAttributes.Interface) {
				output.Write("interface", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			}
			WriteEnum(type.Attributes & TypeAttributes.VisibilityMask, typeVisibility);
			WriteEnum(type.Attributes & TypeAttributes.LayoutMask, typeLayout);
			WriteEnum(type.Attributes & TypeAttributes.StringFormatMask, typeStringFormat);
			const TypeAttributes masks = TypeAttributes.ClassSemanticMask | TypeAttributes.VisibilityMask | TypeAttributes.LayoutMask | TypeAttributes.StringFormatMask;
			WriteFlags(type.Attributes & ~masks, typeAttributes);

			WriteTypeName(type);
			WriteTypeParameters(type);
			output.WriteLine();

			if (type.BaseType != null) {
				output.IncreaseIndent();
				output.Write("extends", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				type.BaseType.WriteTo(output, sb, ILNameSyntax.TypeName);
				output.WriteLine();
				output.DecreaseIndent();
			}
			if (type.HasInterfaces) {
				output.IncreaseIndent();
				for (int index = 0; index < type.Interfaces.Count; index++) {
					if (index > 0)
						output.WriteLine(",", BoxedTextColor.Punctuation);
					if (index == 0) {
						output.Write("implements", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
					else
						output.Write("           ", BoxedTextColor.Text);
					type.Interfaces[index].Interface.WriteTo(output, sb, ILNameSyntax.TypeName);
				}
				output.WriteLine();
				output.DecreaseIndent();
			}

			var bh1 = BracePairHelper.Create(output, "{", CodeBracesRangeFlags.TypeBraces);
			output.WriteLine();
			output.IncreaseIndent();
			bool oldIsInType = isInType;
			isInType = true;
			WriteAttributes(type.CustomAttributes);
			WriteSecurityDeclarations(type);

			for (int i = 0; i < type.GenericParameters.Count; i++)
				WriteGenericParameterAttributes(type.GenericParameters[i]);

			if (type.HasClassLayout) {
				output.Write(".pack", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(numberFormatter.Format(type.PackingSize), type.PackingSize, numberFlags, BoxedTextColor.Number);
				output.WriteLine();
				output.Write(".size", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(numberFormatter.Format(type.ClassSize), type.ClassSize, numberFlags, BoxedTextColor.Number);
				output.WriteLine();
				output.WriteLine();
			}

			for (var i = 0; i < type.Interfaces.Count; i++) {
				var iface = type.Interfaces[i];
				if (iface.HasCustomAttributes) {
					output.Write(".interfaceimpl", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("type", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					iface.Interface.WriteTo(output, sb, ILNameSyntax.TypeName);
					output.WriteLine();
					WriteAttributes(iface.CustomAttributes);
				}
			}

			int membersLeft = type.NestedTypes.Count + type.Fields.Count + type.Methods.Count + type.Events.Count + type.Properties.Count;
			if (type.HasNestedTypes) {
				output.WriteLine("// Nested Types", BoxedTextColor.Comment);
				var nestedTypes = type.GetNestedTypes(options.SortMembers);
				for (int i = 0; i < nestedTypes.Count; i++) {
					options.CancellationToken.ThrowIfCancellationRequested();
					DisassembleType(nestedTypes[i], addLineSep: addLineSep && --membersLeft > 0);
					output.WriteLine();
				}
				output.WriteLine();
			}
			if (type.HasFields) {
				output.WriteLine("// Fields", BoxedTextColor.Comment);
				var fields = type.GetFields(options.SortMembers);
				for (int i = 0; i < fields.Count; i++) {
					options.CancellationToken.ThrowIfCancellationRequested();
					membersLeft--;
					DisassembleField(fields[i]);
				}
				if (addLineSep && membersLeft > 0)
					output.AddLineSeparator(output.Length - 2);
				output.WriteLine();
			}
			if (type.HasMethods) {
				output.WriteLine("// Methods", BoxedTextColor.Comment);
				var methods = type.GetMethods(options.SortMembers);
				for (int i = 0; i < methods.Count; i++) {
					options.CancellationToken.ThrowIfCancellationRequested();
					DisassembleMethod(methods[i], addLineSep: addLineSep && --membersLeft > 0);
					output.WriteLine();
				}
			}
			if (type.HasEvents) {
				output.WriteLine("// Events", BoxedTextColor.Comment);
				var events = type.GetEvents(options.SortMembers);
				for (int i = 0; i < events.Count; i++) {
					options.CancellationToken.ThrowIfCancellationRequested();
					DisassembleEvent(events[i], addLineSep: addLineSep && --membersLeft > 0);
					output.WriteLine();
				}
			}
			if (type.HasProperties) {
				output.WriteLine("// Properties", BoxedTextColor.Comment);
				var properties = type.GetProperties(options.SortMembers);
				for (int i = 0; i < properties.Count; i++) {
					options.CancellationToken.ThrowIfCancellationRequested();
					DisassembleProperty(properties[i], addLineSep: addLineSep && --membersLeft > 0);
				}
				output.WriteLine();
			}

			sb.Clear();
			sb.Append("end of class ");
			if (!UTF8String.IsNullOrEmpty(type.Namespace)) {
				sb.Append(IdentifierEscaper.Escape(type.Namespace));
				sb.Append('.');
			}
			sb.Append(IdentifierEscaper.Escape(type.Name));
			CloseBlock(bh1, addLineSep, sb.ToString());
			isInType = oldIsInType;
		}

		void WriteTypeParameters(ITypeOrMethodDef p)
		{
			if (p.HasGenericParameters) {
				var bh2 = BracePairHelper.Create(output, "<", CodeBracesRangeFlags.AngleBrackets);
				for (int i = 0; i < p.GenericParameters.Count; i++) {
					if (i > 0) {
						output.Write(",", BoxedTextColor.Punctuation);
						output.Write(" ", BoxedTextColor.Text);
					}
					GenericParam gp = p.GenericParameters[i];
					if (gp.HasReferenceTypeConstraint) {
						output.Write("class", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
					if (gp.HasNotNullableValueTypeConstraint) {
						output.Write("valuetype", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
					if (gp.AllowsByRefLike) {
						output.Write("byreflike", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
					if (gp.HasDefaultConstructorConstraint) {
						output.Write(".ctor", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
					if (gp.HasGenericParamConstraints) {
						var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
						for (int j = 0; j < gp.GenericParamConstraints.Count; j++) {
							if (j > 0) {
								output.Write(",", BoxedTextColor.Punctuation);
								output.Write(" ", BoxedTextColor.Text);
							}
							gp.GenericParamConstraints[j].Constraint.WriteTo(output, sb, ILNameSyntax.TypeName);
						}
						bh1.Write(")");
						output.Write(" ", BoxedTextColor.Text);
					}
					if (gp.IsContravariant) {
						output.Write("-", BoxedTextColor.Operator);
					} else if (gp.IsCovariant) {
						output.Write("+", BoxedTextColor.Operator);
					}
					output.Write(DisassemblerHelpers.Escape(gp.Name), gp, DecompilerReferenceFlags.Definition, CSharpMetadataTextColorProvider.Instance.GetColor(gp));
				}
				bh2.Write(">");
			}
		}
		#endregion

		#region Helper methods

		void WriteImplMap(ImplMap info, string memberName) {
			output.Write("\"" + ILStringEscaper.ConvertStringMaxLength(info.Module == null ? string.Empty : info.Module.Name.String, options.MaxStringLength) + "\"", BoxedTextColor.String);

			if (!string.IsNullOrEmpty(info.Name) && info.Name != memberName) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("as", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("\"" + ILStringEscaper.ConvertStringMaxLength(info.Name, options.MaxStringLength) + "\"", BoxedTextColor.String);
			}

			if (info.IsNoMangle) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("nomangle", BoxedTextColor.Keyword);
			}

			if (info.IsCharSetAnsi) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("ansi", BoxedTextColor.Keyword);
			}
			else if (info.IsCharSetAuto) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("autochar", BoxedTextColor.Keyword);
			}
			else if (info.IsCharSetUnicode) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("unicode", BoxedTextColor.Keyword);
			}

			if (info.SupportsLastError) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("lasterr", BoxedTextColor.Keyword);
			}

			if (info.IsCallConvCdecl) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("cdecl", BoxedTextColor.Keyword);
			}
			else if (info.IsCallConvFastcall) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("fastcall", BoxedTextColor.Keyword);
			}
			else if (info.IsCallConvStdcall) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("stdcall", BoxedTextColor.Keyword);
			}
			else if (info.IsCallConvThiscall) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("thiscall", BoxedTextColor.Keyword);
			}
			else if (info.IsCallConvWinapi) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("winapi", BoxedTextColor.Keyword);
			}

			if (info.IsBestFitEnabled) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("bestfit", BoxedTextColor.Keyword);
				output.Write(":", BoxedTextColor.Punctuation);
				output.Write("on", BoxedTextColor.Keyword);
			}
			else if (info.IsBestFitDisabled) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("bestfit", BoxedTextColor.Keyword);
				output.Write(":", BoxedTextColor.Punctuation);
				output.Write("off", BoxedTextColor.Keyword);
			}

			if (info.IsThrowOnUnmappableCharEnabled) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("charmaperror", BoxedTextColor.Keyword);
				output.Write(":", BoxedTextColor.Punctuation);
				output.Write("on", BoxedTextColor.Keyword);
			}
			else if (info.IsThrowOnUnmappableCharDisabled) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("charmaperror", BoxedTextColor.Keyword);
				output.Write(":", BoxedTextColor.Punctuation);
				output.Write("off", BoxedTextColor.Keyword);
			}
		}

		void WriteAttributes(CustomAttributeCollection attributes) {
			for (int i = 0; i < attributes.Count; i++) {
				var a = attributes[i];
				output.Write(".custom", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				a.Constructor.WriteMethodTo(output, sb);
				uint blobOffset = a.BlobOffset;
				if (a.IsRawBlob) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write("=", BoxedTextColor.Operator);
					output.Write(" ", BoxedTextColor.Text);
					WriteBlob(a.RawData);
				}
				else if (blobOffset != 0 && options.OwnerModule is ModuleDefMD md &&
				    md.Metadata.BlobStream.TryCreateReader(blobOffset, out var reader)) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write("=", BoxedTextColor.Operator);
					output.Write(" ", BoxedTextColor.Text);
					WriteBlob(reader.ToArray());
				}
				output.WriteLine();
			}
		}

		void WriteBlob(byte[] blob)
		{
			var bh1 = BracePairHelper.Create(output, "(", CodeBracesRangeFlags.Parentheses);
			output.IncreaseIndent();

			for (int i = 0; i < blob.Length; i++) {
				if (i % 16 == 0 && i < blob.Length - 1) {
					output.WriteLine();
				} else {
					output.Write(" ", BoxedTextColor.Text);
				}
				var b = blob[i];
				output.Write(b.ToString("x2"), b, numberFlags, BoxedTextColor.Number);
			}

			output.WriteLine();
			output.DecreaseIndent();
			bh1.Write(")");
		}

		BracePairHelper OpenBlock(CodeBracesRangeFlags flags)
		{
			output.WriteLine();
			var bh1 = BracePairHelper.Create(output, "{", flags);
			output.WriteLine();
			output.IncreaseIndent();
			return bh1;
		}

		int CloseBlock(BracePairHelper bh1, bool addLineSep = false, string comment = null)
		{
			output.DecreaseIndent();
			bh1.Write("}");
			int endPosition = output.NextPosition;
			if (comment != null) {
				output.Write(" ", BoxedTextColor.Text);
				output.Write("// " + comment, BoxedTextColor.Comment);
			}
			if (addLineSep)
				output.AddLineSeparator(output.NextPosition);
			output.WriteLine();
			return endPosition;
		}

		void WriteFlags<T>(T flags, EnumNameCollection<T> flagNames) where T : struct
		{
			long val = Convert.ToInt64(flags);
			long tested = 0;
			foreach (var pair in flagNames) {
				tested |= pair.Key;
				if ((val & pair.Key) != 0 && pair.Value != null) {
					string[] kvs = pair.Value.Split(' ');
					for (int i = 0; i < kvs.Length; i++) {
						output.Write(kvs[i], BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}
				}
			}
			if ((val & ~tested) != 0) {
				output.Write("flags", BoxedTextColor.Keyword);
				int leftStart = output.NextPosition;
				output.Write("(", BoxedTextColor.Keyword);
				output.Write($"{val & ~tested:x4}", BoxedTextColor.Keyword);
				int rightStart = output.NextPosition;
				output.Write(")", BoxedTextColor.Keyword);
				output.AddBracePair(new TextSpan(leftStart, 1), new TextSpan(rightStart, 1), CodeBracesRangeFlags.Parentheses);
				output.Write(" ", BoxedTextColor.Text);
			}
		}

		void WriteEnum<T>(T enumValue, EnumNameCollection<T> enumNames) where T : struct
		{
			long val = Convert.ToInt64(enumValue);
			foreach (var pair in enumNames) {
				if (pair.Key == val) {
					if (pair.Value != null) {
						string[] kvs = pair.Value.Split(' ');
						for (int i = 0; i < kvs.Length; i++) {
							output.Write(kvs[i], BoxedTextColor.Keyword);
							output.Write(" ", BoxedTextColor.Text);
						}
					}
					return;
				}
			}
			if (val != 0) {
				output.Write("flags", BoxedTextColor.Keyword);
				int leftStart = output.NextPosition;
				output.Write("(", BoxedTextColor.Keyword);
				output.Write($"{val:x4}", BoxedTextColor.Keyword);
				int rightStart = output.NextPosition;
				output.Write(")", BoxedTextColor.Keyword);
				output.AddBracePair(new TextSpan(leftStart, 1), new TextSpan(rightStart, 1), CodeBracesRangeFlags.Parentheses);
				output.Write(" ", BoxedTextColor.Text);
			}

		}

		sealed class EnumNameCollection<T> : IEnumerable<KeyValuePair<long, string>> where T : struct
		{
			readonly List<KeyValuePair<long, string>> names = new List<KeyValuePair<long, string>>();

			public void Add(T flag, string name) => names.Add(new KeyValuePair<long, string>(Convert.ToInt64(flag), name));

			public IEnumerator<KeyValuePair<long, string>> GetEnumerator() => names.GetEnumerator();

			System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => names.GetEnumerator();
		}
		#endregion

		public void WriteAssemblyHeader(AssemblyDef asm)
		{
			output.Write(".assembly", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			if (asm.IsContentTypeWindowsRuntime) {
				output.Write("windowsruntime", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
			}
			output.Write(DisassemblerHelpers.Escape(asm.Name), BoxedTextColor.Text);
			var bh1 = OpenBlock(CodeBracesRangeFlags.OtherBlockBraces);
			WriteAttributes(asm.CustomAttributes);
			WriteSecurityDeclarations(asm);
			if (asm.PublicKey != null && !asm.PublicKey.IsNullOrEmpty) {
				output.Write(".publickey", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("=", BoxedTextColor.Operator);
				output.Write(" ", BoxedTextColor.Text);
				WriteBlob(asm.PublicKey.Data);
				output.WriteLine();
			}
			if (asm.HashAlgorithm != AssemblyHashAlgorithm.None) {
				output.Write(".hash", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("algorithm", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(numberFormatter.Format((uint)asm.HashAlgorithm), (uint)asm.HashAlgorithm, numberFlags, BoxedTextColor.Number);
				if (asm.HashAlgorithm == AssemblyHashAlgorithm.SHA1) {
					output.Write(" ", BoxedTextColor.Text);
					output.Write("// SHA1", BoxedTextColor.Comment);
				}
				output.WriteLine();
			}
			Version v = asm.Version;
			if (v != null) {
				output.Write(".ver", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write(v.Major.ToString(), v.Major, numberFlags, BoxedTextColor.Number);
				output.Write(":", BoxedTextColor.Operator);
				output.Write(v.Minor.ToString(), v.Minor, numberFlags, BoxedTextColor.Number);
				output.Write(":", BoxedTextColor.Operator);
				output.Write(v.Build.ToString(), v.Build, numberFlags, BoxedTextColor.Number);
				output.Write(":", BoxedTextColor.Operator);
				output.Write(v.Revision.ToString(), v.Revision, numberFlags, BoxedTextColor.Number);
				output.WriteLine();
			}
			CloseBlock(bh1);
		}

		public void WriteAssemblyReferences(ModuleDef module)
		{
			if (module == null)
				return;
			foreach (var mref in module.GetModuleRefs()) {
				output.Write(".module", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("extern", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				output.WriteLine(DisassemblerHelpers.Escape(mref.Name), BoxedTextColor.Text);
			}
			foreach (var aref in module.GetAssemblyRefs()) {
				AddTokenComment(aref);
				output.Write(".assembly", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				output.Write("extern", BoxedTextColor.Keyword);
				output.Write(" ", BoxedTextColor.Text);
				if (aref.IsContentTypeWindowsRuntime) {
					output.Write("windowsruntime", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
				}
				output.Write(DisassemblerHelpers.Escape(aref.Name), BoxedTextColor.Text);
				var bh1 = OpenBlock(CodeBracesRangeFlags.OtherBlockBraces);
				if (!PublicKeyBase.IsNullOrEmpty2(aref.PublicKeyOrToken)) {
					output.Write(".publickeytoken", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("=", BoxedTextColor.Operator);
					output.Write(" ", BoxedTextColor.Text);
					WriteBlob(aref.PublicKeyOrToken.Token.Data);
					output.WriteLine();
				}
				if (aref.Version != null) {
					output.Write(".ver", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write(aref.Version.Major.ToString(), aref.Version.Major, numberFlags, BoxedTextColor.Number);
					output.Write(":", BoxedTextColor.Operator);
					output.Write(aref.Version.Minor.ToString(), aref.Version.Minor, numberFlags, BoxedTextColor.Number);
					output.Write(":", BoxedTextColor.Operator);
					output.Write(aref.Version.Build.ToString(), aref.Version.Build, numberFlags, BoxedTextColor.Number);
					output.Write(":", BoxedTextColor.Operator);
					output.Write(aref.Version.Revision.ToString(), aref.Version.Revision, numberFlags, BoxedTextColor.Number);
					output.WriteLine();
				}
				CloseBlock(bh1);
			}
		}

		public void WriteModuleHeader(ModuleDef module)
		{
			if (module.HasExportedTypes) {
				for (int i = 0; i < module.ExportedTypes.Count; i++) {
					var exportedType = module.ExportedTypes[i];
					AddTokenComment(exportedType);
					output.Write(".class", BoxedTextColor.ILDirective);
					output.Write(" ", BoxedTextColor.Text);
					output.Write("extern", BoxedTextColor.Keyword);
					output.Write(" ", BoxedTextColor.Text);
					if (exportedType.IsForwarder) {
						output.Write("forwarder", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
					}

					if (!UTF8String.IsNullOrEmpty(exportedType.Namespace)) {
						DisassemblerHelpers.WriteNamespace(output, exportedType.Namespace, exportedType.DefinitionAssembly, sb);
						output.Write(".", BoxedTextColor.Operator);
					}
					output.Write(IdentifierEscaper.Escape(exportedType.Name), exportedType, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(exportedType));

					var bh1 = OpenBlock(CodeBracesRangeFlags.OtherBlockBraces);
					if (exportedType.DeclaringType != null) {
						output.Write(".class", BoxedTextColor.ILDirective);
						output.Write(" ", BoxedTextColor.Text);
						output.Write("extern", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);

						var declType = exportedType.DeclaringType;
						if (!UTF8String.IsNullOrEmpty(declType.Namespace)) {
							DisassemblerHelpers.WriteNamespace(output, declType.Namespace, declType.DefinitionAssembly, sb);
							output.Write(".", BoxedTextColor.Operator);
						}
						output.Write(IdentifierEscaper.Escape(declType.Name), declType, DecompilerReferenceFlags.None, CSharpMetadataTextColorProvider.Instance.GetColor(declType));
					}
					else {
						output.Write(".assembly", BoxedTextColor.ILDirective);
						output.Write(" ", BoxedTextColor.Text);
						output.Write("extern", BoxedTextColor.Keyword);
						output.Write(" ", BoxedTextColor.Text);
						output.Write(DisassemblerHelpers.Escape(exportedType.Scope.GetScopeName()), exportedType.Scope, DecompilerReferenceFlags.None, BoxedTextColor.Text);
					}
					output.WriteLine();
					CloseBlock(bh1);
				}
			}

			output.Write(".module", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			output.WriteLine(DisassemblerHelpers.Escape(module.Name), BoxedTextColor.Text);
			if (module.Mvid.HasValue)
				output.WriteLine(string.Format("// MVID: {0}", module.Mvid.Value.ToString("B").ToUpperInvariant()), BoxedTextColor.Comment);

			if (module is ModuleDefMD moduleDefMd) {
				output.Write(".imagebase", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				var imageBase = moduleDefMd.Metadata.PEImage.ImageNTHeaders.OptionalHeader.ImageBase;
				output.Write(numberFormatter.Format(imageBase), imageBase, numberFlags, BoxedTextColor.Number);
				output.WriteLine();

				output.Write(".file alignment", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				uint fileAlignment = moduleDefMd.Metadata.PEImage.ImageNTHeaders.OptionalHeader.FileAlignment;
				output.Write(numberFormatter.Format(fileAlignment), fileAlignment, numberFlags, BoxedTextColor.Number);
				output.WriteLine();

				output.Write(".stackreserve", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				ulong sizeOfStackReserve = moduleDefMd.Metadata.PEImage.ImageNTHeaders.OptionalHeader.SizeOfStackReserve;
				output.Write(numberFormatter.Format(sizeOfStackReserve), sizeOfStackReserve, numberFlags, BoxedTextColor.Number);
				output.WriteLine();

				output.Write(".subsystem", BoxedTextColor.ILDirective);
				output.Write(" ", BoxedTextColor.Text);
				ushort subsystem = (ushort)moduleDefMd.Metadata.PEImage.ImageNTHeaders.OptionalHeader.Subsystem;
				output.Write(numberFormatter.Format(subsystem), subsystem, numberFlags, BoxedTextColor.Number);
				output.Write(" ", BoxedTextColor.Text);
				output.WriteLine(string.Format("// {0}", moduleDefMd.Metadata.PEImage.ImageNTHeaders.OptionalHeader.Subsystem.ToString()), BoxedTextColor.Comment);
			}

			output.Write(".corflags", BoxedTextColor.ILDirective);
			output.Write(" ", BoxedTextColor.Text);
			uint cor20HeaderFlags = (uint)module.Cor20HeaderFlags;
			output.Write(numberFormatter.Format(cor20HeaderFlags), cor20HeaderFlags, numberFlags, BoxedTextColor.Number);
			output.Write(" ", BoxedTextColor.Text);
			output.WriteLine(string.Format("// {0}", module.Cor20HeaderFlags.ToString()), BoxedTextColor.Comment);

			WriteAttributes(module.CustomAttributes);
		}

		public void WriteModuleContents(ModuleDef module) {
			for (int i = 0; i < module.Types.Count; i++) {
				DisassembleType(module.Types[i]);
				output.WriteLine();
			}
		}
	}
}
