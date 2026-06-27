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

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.Contracts.Decompiler.XmlDoc {
	/// <summary>
	/// Provides XML documentation tags.
	/// </summary>
	public sealed class XmlDocKeyProvider {
		/// <summary>
		/// Gets an XML doc key
		/// </summary>
		/// <param name="member">Member</param>
		/// <param name="b">String builder</param>
		/// <returns></returns>
		public static StringBuilder? GetKey(IMemberRef? member, StringBuilder b) => GetKey(member, b, XmlDocCompiler.RoslynOrLegacy);

		/// <summary>
		/// Gets an XML doc key
		/// </summary>
		/// <param name="member">Member</param>
		/// <param name="b">String builder</param>
		/// <param name="compiler">Compiler which was used to generate the XML doc file</param>
		/// <returns></returns>
		public static StringBuilder? GetKey(IMemberRef? member, StringBuilder b, XmlDocCompiler compiler) {
			if (member is null)
				return null;
			b.Clear();
			if (member is ITypeDefOrRef defOrRef) {
				b.Append("T:");
				AppendTypeName(b, defOrRef);
			}
			else {
				if (member.IsField)
					b.Append("F:");
				else if (member.IsPropertyDef)
					b.Append("P:");
				else if (member.IsEventDef)
					b.Append("E:");
				else if (member.IsMethod)
					b.Append("M:");
				if (member.DeclaringType is not null) {
					AppendTypeName(b, member.DeclaringType);
					b.Append('.');
				}
				if (IsExplicitInterfaceImplementation(member, out var declType)) {
					AppendTypeName(b, declType, compiler, true);
					b.Append('#');
				}
				var nameAsSystemString = UTF8String.ToSystemStringOrEmpty(member.Name);
				for (var i = 0; i < nameAsSystemString.Length; i++) {
					char c = nameAsSystemString[i];
					b.Append(c switch {
						'.' => '#',
						'<' => '{',
						'>' => '}',
						_ => c
					});
				}
				IList<Parameter>? parameters;
				TypeSig? explicitReturnType = null;
				if (member.IsPropertyDef) {
					parameters = GetParameters((PropertyDef)member).ToList();
				}
				else if (member.IsMethod) {
					var mr = (IMethod)member;
					if (mr.NumberOfGenericParameters > 0) {
						b.Append("``");
						b.Append(mr.NumberOfGenericParameters);
					}
					parameters = mr.GetParameters();
					if (nameAsSystemString == "op_Implicit" || nameAsSystemString == "op_Explicit") {
						explicitReturnType = mr.MethodSig.GetRetType();
					}
				}
				else {
					parameters = null;
				}
				if (parameters is not null && parameters.Any(a => a.IsNormalMethodParameter)) {
					b.Append('(');
					for (int i = 0; i < parameters.Count; i++) {
						var param = parameters[i];
						if (!param.IsNormalMethodParameter)
							continue;
						if (param.MethodSigIndex > 0)
							b.Append(',');
						AppendTypeName(b, param.Type, compiler);
					}
					b.Append(')');
				}
				if (explicitReturnType is not null) {
					b.Append('~');
					AppendTypeName(b, explicitReturnType, compiler);
				}
			}
			return b;
		}

		static bool IsExplicitInterfaceImplementation(IMemberRef member, [NotNullWhen(true)] out TypeSig? type) {
			type = null;
			if (member.Name.IndexOf('.') >= 0)
				return false;
			var method = member switch {
				MethodDef m => m,
				PropertyDef p => p.GetMethod ?? p.SetMethod,
				EventDef e => e.AddMethod ?? e.RemoveMethod ?? e.InvokeMethod,
				_ => null
			};
			if (method is null)
				return false;
			if (method.Overrides.Count != 1)
				return false;
			type = method.Overrides[0].MethodDeclaration.DeclaringType.ToTypeSig();
			return true;
		}

		static IEnumerable<Parameter> GetParameters(PropertyDef property) {
			if (property.GetMethod is not null) {
				foreach (var param in property.GetMethod.Parameters)
					yield return param;
				yield break;
			}
			if (property.SetMethod is not null) {
				int last = property.SetMethod.Parameters.Count - 1;
				foreach (var param in property.SetMethod.Parameters) {
					if (param.Index != last)
						yield return param;
				}
				yield break;
			}

			int i = 0;
			foreach (var param in property.PropertySig.GetParams()) {
				yield return new Parameter(i, i, param);
				i++;
			}
		}

		static void AppendTypeName(StringBuilder b, TypeSig? type, XmlDocCompiler compiler, bool explicitInterfaceImpl = false) {
			if (type is null)
				return;
			// MSVC writes modifiers signatures and pinned to XML doc unlike other compilers.
			if (compiler != XmlDocCompiler.MSVC)
				type = type.RemovePinnedAndModifiers();
			if (type is GenericInstSig giType) {
				AppendTypeNameWithArguments(b, giType.GenericType?.TypeDefOrRef, giType.GenericArguments, compiler, explicitInterfaceImpl);
				return;
			}
			if (type is ArraySigBase arrayType) {
				AppendTypeName(b, arrayType.Next, compiler, explicitInterfaceImpl);
				b.Append('[');
				var lowerBounds = arrayType.GetLowerBounds();
				var sizes = arrayType.GetSizes();
				for (int i = 0; i < arrayType.Rank; i++) {
					if (i > 0)
						b.Append(',');
					if (!explicitInterfaceImpl && i < lowerBounds.Count) {
						b.Append(lowerBounds[i]);
						b.Append(':');
						if (i < sizes.Count)
							b.Append(sizes[i] + lowerBounds[i] - 1);
					}
				}
				b.Append(']');
				return;
			}
			if (type is ByRefSig refType) {
				AppendTypeName(b, refType.Next, compiler, explicitInterfaceImpl);
				b.Append('@');
				return;
			}
			if (type is PtrSig ptrType) {
				AppendTypeName(b, ptrType.Next, compiler, explicitInterfaceImpl);
				b.Append('*');
				return;
			}
			if (type is GenericSig gp) {
				if (explicitInterfaceImpl && gp.GenericParam?.Name is not null) {
					b.Append(gp.GenericParam.Name);
					return;
				}
				char c = explicitInterfaceImpl ? '!' : '`';
				b.Append(c);
				if (gp.IsMethodVar)
					b.Append(c);
				b.Append(gp.Number);
				return;
			}
			if (type is CModOptSig cModOptSig) {
				AppendTypeName(b, cModOptSig.Next, compiler);
				b.Append('!');
				AppendTypeName(b, cModOptSig.Modifier);
				return;
			}
			if (type is CModReqdSig cModReqdSig) {
				AppendTypeName(b, cModReqdSig.Next, compiler);
				b.Append('|');
				AppendTypeName(b, cModReqdSig.Modifier);
				return;
			}
			if (type is PinnedSig pinnedSig) {
				AppendTypeName(b, pinnedSig.Next, compiler);
				b.Append('^');
				return;
			}
			if (type is FnPtrSig fnPtrSig) {
				if (compiler == XmlDocCompiler.MSVC) {
					b.Append("=FUNC:");
					AppendTypeName(b, fnPtrSig.MethodSig.RetType, compiler);
					if (fnPtrSig.MethodSig.Params.Count > 0) {
						b.Append('(');
						for (var i = 0; i < fnPtrSig.MethodSig.Params.Count; i++) {
							if (i != 0)
								b.Append(',');
							AppendTypeName(b, fnPtrSig.MethodSig.Params[i], compiler);
						}
						b.Append(')');
					}
				}
				else if (compiler == XmlDocCompiler.RoslynOrLegacy) {
					// TODO: Roslyn just writes an empty string for function pointer signatures.
					// See https://github.com/dotnet/roslyn/issues/48363
					// Legacy compiler does not emit function pointer signatures.
				}
				return;
			}

			AppendTypeName(b, type.ToTypeDefOrRef(), explicitInterfaceImpl);
		}

		static void AppendTypeName(StringBuilder b, ITypeDefOrRef typeDefOrRef, bool explicitInterfaceImpl = false) {
			if (typeDefOrRef.DeclaringType is not null) {
				AppendTypeName(b, typeDefOrRef.DeclaringType);
				b.Append(explicitInterfaceImpl ? '#' : '.');
				b.Append(typeDefOrRef.Name);
			}
			else if (explicitInterfaceImpl)
				b.Append(FullNameFactory.FullName(typeDefOrRef, false, null, null).Replace('.', '#'));
			else
				FullNameFactory.FullNameSB(typeDefOrRef, false, null, b);
		}

		static int AppendTypeNameWithArguments(StringBuilder b, ITypeDefOrRef? type, IList<TypeSig> genericArguments, XmlDocCompiler compiler, bool explicitInterfaceImpl) {
			if (type is null)
				return 0;
			int outerTypeParameterCount = 0;
			if (type.DeclaringType is not null) {
				outerTypeParameterCount = AppendTypeNameWithArguments(b, type.DeclaringType, genericArguments, compiler, explicitInterfaceImpl);
				b.Append(explicitInterfaceImpl ? '#' : '.');
			}
			else {
				int len = b.Length;
				FullNameFactory.NamespaceSB(type, true, b);
				if (len != b.Length)
					b.Append(explicitInterfaceImpl ? '#' : '.');
			}
			b.Append(SplitTypeParameterCountFromReflectionName(UTF8String.ToSystemStringOrEmpty(type.Name), out int localTypeParameterCount));

			if (localTypeParameterCount > 0) {
				int totalTypeParameterCount = outerTypeParameterCount + localTypeParameterCount;
				b.Append('{');
				for (int i = outerTypeParameterCount; i < totalTypeParameterCount && i < genericArguments.Count; i++) {
					if (i > outerTypeParameterCount)
						b.Append(explicitInterfaceImpl ? '@' : ',');
					AppendTypeName(b, genericArguments[i], compiler, explicitInterfaceImpl);
				}
				b.Append('}');
			}
			return outerTypeParameterCount + localTypeParameterCount;
		}

		static string SplitTypeParameterCountFromReflectionName(string reflectionName, out int typeParameterCount) {
			int pos = reflectionName.LastIndexOf('`');
			if (pos < 0) {
				typeParameterCount = 0;
				return reflectionName;
			}

			string typeCount = reflectionName.Substring(pos + 1);
			if (int.TryParse(typeCount, out typeParameterCount))
				return reflectionName.Substring(0, pos);
			return reflectionName;
		}

		/// <summary>
		/// Finds a member by key
		/// </summary>
		/// <param name="module">Module to search</param>
		/// <param name="key">Key</param>
		/// <returns></returns>
		public static IMemberRef? FindMemberByKey(ModuleDef module, string? key) => FindMemberByKey(module, key, XmlDocCompiler.RoslynOrLegacy);

		/// <summary>
		/// Finds a member by key
		/// </summary>
		/// <param name="module">Module to search</param>
		/// <param name="key">Key</param>
		/// <param name="compiler">Compiler which was used to generate the XML doc file</param>
		/// <returns></returns>
		public static IMemberRef? FindMemberByKey(ModuleDef module, string? key, XmlDocCompiler compiler) {
			if (module is null)
				throw new ArgumentNullException(nameof(module));
			if (key is null || key.Length < 2 || key[1] != ':')
				return null;
			return key[0] switch {
				'T' => FindType(module, key.Substring(2)),
				'F' => FindMember(module, key, type => type.Fields, compiler),
				'P' => FindMember(module, key, type => type.Properties, compiler),
				'E' => FindMember(module, key, type => type.Events, compiler),
				'M' => FindMember(module, key, type => type.Methods, compiler),
				_ => null
			};
		}

		static IMemberRef? FindMember(ModuleDef module, string key, Func<TypeDef, IEnumerable<IMemberRef>> memberSelector, XmlDocCompiler compiler) {
			int parenPos = key.IndexOf('(');
			int dotPos;
			if (parenPos > 0)
				dotPos = key.LastIndexOf('.', parenPos - 1, parenPos);
			else
				dotPos = key.LastIndexOf('.');
			if (dotPos < 0)
				return null;
			var type = FindType(module, key.Substring(2, dotPos - 2));
			if (type is null)
				return null;
			string shortName;
			if (parenPos > 0)
				shortName = key.Substring(dotPos + 1, parenPos - (dotPos + 1));
			else
				shortName = key.Substring(dotPos + 1);
			IMemberRef? shortNameMatch = null;
			var sb = new StringBuilder();
			foreach (var member in memberSelector(type)) {
				var memberKey = GetKey(member, sb, compiler);
				if (memberKey?.CheckEquals(key) == true)
					return member;
				if (shortName == UTF8String.ToSystemStringOrEmpty(member.Name).Replace('.', '#'))
					shortNameMatch = member;
			}
			// if there's no match by ID string (key), return the match by name.
			return shortNameMatch;
		}

		static TypeDef? FindType(ModuleDef module, string name) {
			int pos = name.LastIndexOf('.');
			if (string.IsNullOrEmpty(name))
				return null;
			var type = module.Find(name, true);
			if (type is null && pos > 0) { // Original code only entered if ns.Length > 0
										   // try if this is a nested type
				type = FindType(module, name.Substring(0, pos));
				if (type is not null) {
					foreach (var nt in type.NestedTypes) {
						if (nt.Name == name)
							return nt;
					}
					return null;
				}
			}
			return type;
		}
	}
}
