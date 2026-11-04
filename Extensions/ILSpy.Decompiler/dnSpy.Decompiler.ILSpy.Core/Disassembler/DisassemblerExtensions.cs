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
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnlib.PE;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	/// <summary>
	/// The dnlib extension methods the IL disassembler used from the old decompiler (DnlibExtensions)
	/// </summary>
	static class DisassemblerExtensions {
		sealed class InterfaceImplComparer : IComparer<InterfaceImpl> {
			public static readonly InterfaceImplComparer Instance = new InterfaceImplComparer();

			public int Compare(InterfaceImpl x, InterfaceImpl y) {
				int c = StringComparer.OrdinalIgnoreCase.Compare(x.Interface.Name, y.Interface.Name);
				if (c != 0)
					return c;
				c = x.MDToken.Raw.CompareTo(y.MDToken.Raw);
				if (c != 0)
					return c;
				return x.GetHashCode().CompareTo(y.GetHashCode());
			}
		}

		public static IList<InterfaceImpl> GetInterfaceImpls(this TypeDef type, bool sortMembers) {
			if (!sortMembers)
				return type.Interfaces;
			var ary = type.Interfaces.ToArray();
			Array.Sort(ary, InterfaceImplComparer.Instance);
			return ary;
		}

		public static IList<TypeDef> GetNestedTypes(this TypeDef type, bool sortMembers) {
			if (!sortMembers)
				return type.NestedTypes;
			var ary = type.NestedTypes.ToArray();
			Array.Sort(ary, TypeDefComparer.Instance);
			return ary;
		}

		public static IList<FieldDef> GetFields(this TypeDef type, bool sortMembers) {
			if (!sortMembers || !type.CanSortFields())
				return type.Fields;
			var ary = type.Fields.ToArray();
			Array.Sort(ary, FieldDefComparer.Instance);
			return ary;
		}

		public static IList<EventDef> GetEvents(this TypeDef type, bool sortMembers) {
			if (!sortMembers || !type.CanSortMethods())
				return type.Events;
			var ary = type.Events.ToArray();
			Array.Sort(ary, EventDefComparer.Instance);
			return ary;
		}

		public static IList<PropertyDef> GetProperties(this TypeDef type, bool sortMembers) {
			if (!sortMembers || !type.CanSortMethods())
				return type.Properties;
			var ary = type.Properties.ToArray();
			Array.Sort(ary, PropertyDefComparer.Instance);
			return ary;
		}

		public static IList<MethodDef> GetMethods(this TypeDef type, bool sortMembers) {
			if (!sortMembers || !type.CanSortMethods())
				return type.Methods;
			var ary = type.Methods.ToArray();
			Array.Sort(ary, MethodDefComparer.Instance);
			return ary;
		}

		public static int GetEndOffset(this Instruction inst) {
			if (inst == null)
				return 0;
			return (int)inst.Offset + inst.GetSize();
		}

		public static Instruction GetPrevious(this CilBody body, Instruction instr) {
			int index = body.Instructions.IndexOf(instr);
			if (index <= 0)
				return null;
			return body.Instructions[index - 1];
		}

		public static IList<TypeSig> GetParameters(this MethodBaseSig methodSig) {
			if (methodSig == null)
				return new List<TypeSig>();
			if (methodSig.ParamsAfterSentinel != null)
				return methodSig.Params
					.Concat(new TypeSig[] { new SentinelSig() })
					.Concat(methodSig.ParamsAfterSentinel)
					.ToList();
			else
				return methodSig.Params;
		}

		public static IEnumerable<Parameter> GetParameters(this PropertyDef property) {
			if (property == null)
				yield break;
			if (property.GetMethod != null) {
				for (int i = 0; i < property.GetMethod.Parameters.Count; i++)
					yield return property.GetMethod.Parameters[i];
				yield break;
			}
			if (property.SetMethod != null) {
				int last = property.SetMethod.Parameters.Count - 1;
				for (int i = 0; i < property.SetMethod.Parameters.Count; i++) {
					var param = property.SetMethod.Parameters[i];
					if (param.Index != last)
						yield return param;
				}
				yield break;
			}

			var sigs = property.PropertySig.GetParameters();
			for (int i = 0; i < sigs.Count; i++)
				yield return new Parameter(i, i, sigs[i]);
		}

		public static string GetScopeName(this IScope scope) {
			if (scope == null)
				return string.Empty;
			if (scope is IFullName fullName)
				return fullName.Name;
			return scope.ScopeName; // Shouldn't be reached
		}

		static int GetParametersSkip(this IList<Parameter> parameters) {
			if (parameters == null || parameters.Count == 0)
				return 0;
			if (parameters[0].IsHiddenThisParameter)
				return 1;
			return 0;
		}

		public static IEnumerable<Parameter> SkipNonNormal(this IList<Parameter> parameters) {
			if (parameters == null)
				yield break;
			for (int i = 0; i < parameters.Count; i++) {
				var p = parameters[i];
				if (p.IsNormalMethodParameter)
					yield return p;
			}
		}

		public static int GetNumberOfNormalParameters(this IList<Parameter> parameters) {
			if (parameters == null)
				return 0;
			return parameters.Count - GetParametersSkip(parameters);
		}

		public static ImageSectionHeader GetContainingSection(this ModuleDef mod, RVA rva) {
			if (mod is not ModuleDefMD mdMod)
				return null;
			var image = mdMod.Metadata.PEImage;
			for (int i = 0; i < image.ImageSectionHeaders.Count; i++) {
				var section = image.ImageSectionHeaders[i];
				if (rva >= section.VirtualAddress &&
					rva < section.VirtualAddress + Math.Max(section.VirtualSize, section.SizeOfRawData))
					return section;
			}
			return null;
		}
	}
}
