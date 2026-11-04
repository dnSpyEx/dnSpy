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
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.ILSpy.Core.DebugInfo {
	/// <summary>
	/// The namespaces of the types a type and its nested types use (signatures, attributes, base types and method
	/// bodies). The expression evaluator resolves type names with the method's imports, and a method decompiled on its
	/// own (the debugger's case) only has the usings its own code needs, so the imports also get the namespaces that
	/// the whole type's code would have usings for.
	/// </summary>
	static class TypeNamespaces {
		public static HashSet<string> Get(TypeDef type) {
			while (type.DeclaringType is not null)
				type = type.DeclaringType;
			var collector = new Collector();
			foreach (var t in type.GetTypes())
				collector.Add(t);
			collector.Add(type);
			return collector.Namespaces;
		}

		sealed class Collector {
			public readonly HashSet<string> Namespaces = new HashSet<string>(StringComparer.Ordinal);
			readonly HashSet<object> seen = new HashSet<object>();
			int depth;

			public void Add(TypeDef type) {
				Add(type.BaseType);
				foreach (var iface in type.Interfaces)
					Add(iface.Interface);
				Add(type.CustomAttributes);
				foreach (var gp in type.GenericParameters) {
					foreach (var c in gp.GenericParamConstraints)
						Add(c.Constraint);
				}
				foreach (var field in type.Fields) {
					Add(field.FieldSig?.Type);
					Add(field.CustomAttributes);
				}
				foreach (var property in type.Properties) {
					Add(property.PropertySig);
					Add(property.CustomAttributes);
				}
				foreach (var ev in type.Events) {
					Add(ev.EventType);
					Add(ev.CustomAttributes);
				}
				foreach (var method in type.Methods)
					Add(method);
			}

			void Add(MethodDef method) {
				Add(method.MethodSig);
				Add(method.CustomAttributes);
				foreach (var pd in method.ParamDefs)
					Add(pd.CustomAttributes);
				foreach (var gp in method.GenericParameters) {
					foreach (var c in gp.GenericParamConstraints)
						Add(c.Constraint);
				}
				if (method.Body is not CilBody body)
					return;
				foreach (var local in body.Variables)
					Add(local.Type);
				foreach (var eh in body.ExceptionHandlers)
					Add(eh.CatchType);
				foreach (var instr in body.Instructions) {
					switch (instr.Operand) {
					case ITypeDefOrRef t: Add(t); break;
					case IMethod m: Add(m); break;
					case IField f: Add(f); break;
					case MethodSig sig: Add(sig); break;
					}
				}
			}

			void Add(CustomAttributeCollection attributes) {
				foreach (var ca in attributes)
					Add(ca.AttributeType);
			}

			void Add(IMethod method) {
				if (!seen.Add(method))
					return;
				Add(method.DeclaringType);
				Add(method.MethodSig);
				if (method is MethodSpec spec && spec.GenericInstMethodSig is GenericInstMethodSig gims) {
					foreach (var arg in gims.GenericArguments)
						Add(arg);
				}
			}

			void Add(IField field) {
				if (!seen.Add(field))
					return;
				Add(field.DeclaringType);
				Add(field.FieldSig?.Type);
			}

			void Add(MethodBaseSig? sig) {
				if (sig is null)
					return;
				Add(sig.RetType);
				foreach (var p in sig.Params)
					Add(p);
			}

			void Add(ITypeDefOrRef? type) {
				if (type is null || !seen.Add(type))
					return;
				if (type is TypeSpec spec) {
					Add(spec.TypeSig);
					return;
				}
				// Nested types are used by their top-level type's name
				var topLevel = type;
				for (int i = 0; i < 100 && topLevel.DeclaringType is ITypeDefOrRef declaringType; i++)
					topLevel = declaringType;
				var ns = topLevel.Namespace;
				if (!string.IsNullOrEmpty(ns))
					Namespaces.Add(ns);
			}

			void Add(TypeSig? sig) {
				// (Malformed metadata can nest signatures deeply)
				if (sig is null || depth > 100)
					return;
				depth++;
				try {
					switch (sig) {
					case TypeDefOrRefSig tdr: Add(tdr.TypeDefOrRef); break;
					case GenericInstSig gis:
						Add(gis.GenericType);
						foreach (var arg in gis.GenericArguments)
							Add(arg);
						break;
					case FnPtrSig fnPtr: Add(fnPtr.MethodSig); break;
					case ModifierSig mod:
						Add(mod.Modifier);
						Add(mod.Next);
						break;
					default: Add(sig.Next); break;
					}
				}
				finally {
					depth--;
				}
			}
		}
	}
}
