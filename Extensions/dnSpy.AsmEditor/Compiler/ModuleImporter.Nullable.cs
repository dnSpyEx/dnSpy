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
using System.Diagnostics;
using System.Linq;
using dnlib.DotNet;
using dnSpy.AsmEditor.Types;

namespace dnSpy.AsmEditor.Compiler {
	sealed partial class ModuleImporter {
		/// <summary>
		/// Edit Method: the type declarations aren't edited, a merged type whose declaration is the same keeps its
		/// original nullable metadata. (The decompiled declaration doesn't always have the original annotations, e.g. a
		/// type parameter that's notnull because the [Nullable] attributes of private types were removed (.NET's own
		/// assemblies): editing a method of System.Linq.Enumerable changed its nested types' type parameters.)
		/// </summary>
		bool editsOnlyMembers;

		/// <summary>
		/// Keeps the meaning of the nullable metadata ([NullableContext], [Nullable]) of the types the edited code was
		/// merged into:
		/// <list type="bullet">
		/// <item>A type's [NullableContext] stays the original one: the members that aren't replaced (e.g. all the other
		/// members of the type in Edit Method) depend on it. The compiled code's context can be different.</item>
		/// <item>The members, nested types and type declarations that come from the compiled code get their own context
		/// when the compiled one is different from the original one, so they keep the compiled annotations.</item>
		/// <item>An edited member with the same signature gets its original attributes back if the original has
		/// oblivious types (code compiled with nullable annotations disabled: decompiled code has no '#nullable disable'
		/// so the compiler made them not annotated), or if the compiled member has no annotations at all (the code
		/// was compiled with nullable annotations disabled, e.g. the legacy C# decompiler's code, which has no '?').</item>
		/// </list>
		/// </summary>
		void FixNullableMetadata() {
			Debug2.Assert(importSigComparerOptions is not null);
			var comparer = new ImportSigComparer(importSigComparerOptions, SIG_COMPARER_OPTIONS, targetModule);
			foreach (var kv in oldTypeToNewType) {
				if (kv.Value is MergedImportedType mergedType)
					FixNullableMetadata(kv.Key, mergedType, comparer);
			}
		}

		void FixNullableMetadata(TypeDef compiledType, MergedImportedType mergedType, ImportSigComparer comparer) {
			var targetType = mergedType.TargetType!;
			var options = mergedType.NewTypeDefOptions;
			byte compiledContext = NullableMetadata.GetContext(compiledType);
			// The original context, the type's own one is restored below. Its declaring types are merged types too
			// (with their original contexts) or the type is a top-level type.
			byte targetContext = NullableMetadata.GetContext(targetType);

			RemoveAll(options.CustomAttributes, a => a.TypeFullName == NullableMetadata.NullableContextAttribute);
			AddNullableAttributes(options.CustomAttributes, targetType, NullableMetadata.NullableContextAttribute);

			FixTypeDeclaration(compiledType, targetType, options, compiledContext, targetContext, comparer);

			foreach (var nestedType in mergedType.NewOrExistingNestedTypes) {
				if (nestedType is not NewImportedType newType)
					continue;
				if (compiledContext != targetContext && NullableMetadata.GetOwnContext(toExtraData[newType].CompiledType) is null)
					AddNullableAttribute(newType.TargetType!.CustomAttributes, NullableMetadata.NullableContextAttribute, compiledContext);
			}

			foreach (var compiledMethod in compiledType.Methods) {
				if (editedMethodsToFix.TryGetValue(compiledMethod, out var originalMethod)) {
					var editedMethod = oldMethodToNewMethod[compiledMethod].EditedMember;
					if (SameSignature(compiledMethod, originalMethod, comparer) &&
						(HasObliviousPositions(originalMethod, targetContext) || !HasAnnotations(compiledMethod, compiledContext)))
						RestoreNullableAttributes(editedMethod, originalMethod);
					else
						AddNullableContext(editedMethod, compiledContext, targetContext);
				}
			}
			foreach (var method in mergedType.NewMethods)
				AddNullableContext(method, compiledContext, targetContext);

			foreach (var compiledField in compiledType.Fields) {
				if (editedFieldsToFix.TryGetValue(compiledField, out var originalField)) {
					var editedField = oldFieldToNewField[compiledField].EditedMember;
					if (comparer.Equals(compiledField.FieldSig, originalField.FieldSig) &&
						(NullableMetadata.HasObliviousPositions(originalField.FieldSig?.Type, originalField, targetContext) ||
						!NullableMetadata.HasAnnotations(compiledField.FieldSig?.Type, compiledField, compiledContext)))
						ReplaceNullableAttributes(editedField.CustomAttributes, originalField);
					else
						AddNullable(editedField, editedField.FieldSig?.Type, compiledContext, targetContext);
				}
			}
			foreach (var field in mergedType.NewFields)
				AddNullable(field, field.FieldSig?.Type, compiledContext, targetContext);

			foreach (var compiledProperty in compiledType.Properties) {
				if (editedPropertiesToFix.TryGetValue(compiledProperty, out var originalProperty)) {
					var editedProperty = oldPropertyToNewProperty[compiledProperty].EditedMember;
					if (comparer.Equals(compiledProperty.PropertySig, originalProperty.PropertySig) &&
						(NullableMetadata.HasObliviousPositions(originalProperty.PropertySig?.RetType, originalProperty, targetContext) ||
						!NullableMetadata.HasAnnotations(compiledProperty.PropertySig?.RetType, compiledProperty, compiledContext)))
						ReplaceNullableAttributes(editedProperty.CustomAttributes, originalProperty);
					else
						AddNullable(editedProperty, editedProperty.PropertySig?.RetType, compiledContext, targetContext);
				}
			}
			foreach (var property in mergedType.NewProperties)
				AddNullable(property, property.PropertySig?.RetType, compiledContext, targetContext);

			foreach (var compiledEvent in compiledType.Events) {
				if (editedEventsToFix.TryGetValue(compiledEvent, out var originalEvent)) {
					var editedEvent = oldEventToNewEvent[compiledEvent].EditedMember;
					if (comparer.Equals(compiledEvent.EventType, originalEvent.EventType) &&
						(NullableMetadata.HasObliviousPositions(originalEvent.EventType.ToTypeSig(), originalEvent, targetContext) ||
						!NullableMetadata.HasAnnotations(compiledEvent.EventType.ToTypeSig(), compiledEvent, compiledContext)))
						ReplaceNullableAttributes(editedEvent.CustomAttributes, originalEvent);
					else
						AddNullable(editedEvent, editedEvent.EventType.ToTypeSig(), compiledContext, targetContext);
				}
			}
			foreach (var evt in mergedType.NewEvents)
				AddNullable(evt, evt.EventType.ToTypeSig(), compiledContext, targetContext);
		}

		/// <summary>
		/// The base type, interfaces and generic parameters of a merged type (the type's own context applies to them)
		/// </summary>
		void FixTypeDeclaration(TypeDef compiledType, TypeDef targetType, TypeDefOptions options, byte compiledContext, byte targetContext, ImportSigComparer comparer) {
			// The original interface of each compiled one (in any order: metadata sorts them, the compiled code has the
			// decompiled order). options.Interfaces are the imported compiled ones, in the same order.
			var originalInterfaces = GetOriginalInterfaces(compiledType, targetType, comparer);
			bool sameDeclaration =
				comparer.Equals(compiledType.BaseType, targetType.BaseType) &&
				originalInterfaces is not null &&
				options.Interfaces.Count == originalInterfaces.Length &&
				SameGenericParameters(compiledType.GenericParameters, targetType.GenericParameters, comparer) &&
				options.GenericParameters.Count == targetType.GenericParameters.Count;
			if (sameDeclaration && (editsOnlyMembers || TypeDeclarationHasObliviousPositions(targetType, targetContext) || !TypeDeclarationHasAnnotations(compiledType, compiledContext))) {
				ReplaceNullableAttributes(options.CustomAttributes, targetType, NullableMetadata.NullableAttribute);
				for (int i = 0; i < options.Interfaces.Count; i++)
					ReplaceNullableAttributes(options.Interfaces[i].CustomAttributes, originalInterfaces![i]);
				ReplaceNullableAttributes(options.GenericParameters, targetType.GenericParameters);
				return;
			}
			if (compiledContext == targetContext)
				return;
			if (options.BaseType is not null)
				AddNullable(options.CustomAttributes, options.BaseType.ToTypeSig(), compiledContext, isBaseType: true);
			foreach (var iface in options.Interfaces)
				AddNullable(iface.CustomAttributes, iface.Interface.ToTypeSig(), compiledContext, isBaseType: true);
			AddNullable(options.GenericParameters, compiledContext);
		}

		static InterfaceImpl[]? GetOriginalInterfaces(TypeDef compiledType, TypeDef targetType, ImportSigComparer comparer) {
			if (compiledType.Interfaces.Count != targetType.Interfaces.Count)
				return null;
			var result = new InterfaceImpl[compiledType.Interfaces.Count];
			var used = new bool[targetType.Interfaces.Count];
			for (int i = 0; i < result.Length; i++) {
				var iface = compiledType.Interfaces[i].Interface;
				int index = -1;
				for (int j = 0; j < targetType.Interfaces.Count; j++) {
					if (!used[j] && comparer.Equals(iface, targetType.Interfaces[j].Interface)) {
						index = j;
						break;
					}
				}
				if (index < 0)
					return null;
				used[index] = true;
				result[i] = targetType.Interfaces[index];
			}
			return result;
		}

		static bool TypeDeclarationHasObliviousPositions(TypeDef type, byte context) {
			if (type.BaseType is not null && NullableMetadata.HasObliviousPositions(type.BaseType.ToTypeSig(), type, context, isBaseType: true))
				return true;
			foreach (var iface in type.Interfaces) {
				if (NullableMetadata.HasObliviousPositions(iface.Interface.ToTypeSig(), iface, context, isBaseType: true))
					return true;
			}
			foreach (var gp in type.GenericParameters) {
				if (NullableMetadata.HasObliviousPositions(gp, context))
					return true;
			}
			return false;
		}

		static bool TypeDeclarationHasAnnotations(TypeDef type, byte context) {
			if (type.BaseType is not null && NullableMetadata.HasAnnotations(type.BaseType.ToTypeSig(), type, context, isBaseType: true))
				return true;
			foreach (var iface in type.Interfaces) {
				if (NullableMetadata.HasAnnotations(iface.Interface.ToTypeSig(), iface, context, isBaseType: true))
					return true;
			}
			foreach (var gp in type.GenericParameters) {
				if (NullableMetadata.HasAnnotations(gp, context))
					return true;
			}
			return false;
		}

		static bool HasAnnotations(MethodDef method, byte typeContext) {
			byte context = NullableMetadata.GetOwnContext(method) ?? typeContext;
			var sig = method.MethodSig;
			if (sig is null)
				return true;
			if (NullableMetadata.HasAnnotations(sig.RetType, GetParamDef(method, 0), context))
				return true;
			for (int i = 0; i < sig.Params.Count; i++) {
				if (NullableMetadata.HasAnnotations(sig.Params[i], GetParamDef(method, i + 1), context))
					return true;
			}
			foreach (var gp in method.GenericParameters) {
				if (NullableMetadata.HasAnnotations(gp, context))
					return true;
			}
			return false;
		}

		static bool SameSignature(MethodDef compiledMethod, MethodDef originalMethod, ImportSigComparer comparer) =>
			comparer.Equals(compiledMethod.MethodSig, originalMethod.MethodSig) &&
			SameGenericParameters(compiledMethod.GenericParameters, originalMethod.GenericParameters, comparer);

		static bool SameGenericParameters(IList<GenericParam> compiled, IList<GenericParam> original, ImportSigComparer comparer) {
			if (compiled.Count != original.Count)
				return false;
			for (int i = 0; i < compiled.Count; i++) {
				var a = compiled[i];
				var b = original[i];
				if (a.Number != b.Number || a.GenericParamConstraints.Count != b.GenericParamConstraints.Count)
					return false;
				for (int j = 0; j < a.GenericParamConstraints.Count; j++) {
					if (!comparer.Equals(a.GenericParamConstraints[j].Constraint, b.GenericParamConstraints[j].Constraint))
						return false;
				}
			}
			return true;
		}

		static bool HasObliviousPositions(MethodDef method, byte typeContext) {
			byte context = NullableMetadata.GetOwnContext(method) ?? typeContext;
			var sig = method.MethodSig;
			if (sig is null)
				return false;
			if (NullableMetadata.HasObliviousPositions(sig.RetType, GetParamDef(method, 0), context))
				return true;
			for (int i = 0; i < sig.Params.Count; i++) {
				if (NullableMetadata.HasObliviousPositions(sig.Params[i], GetParamDef(method, i + 1), context))
					return true;
			}
			foreach (var gp in method.GenericParameters) {
				if (NullableMetadata.HasObliviousPositions(gp, context))
					return true;
			}
			return false;
		}

		static bool HasAnnotatablePositions(MethodDef method) {
			var sig = method.MethodSig;
			if (sig is null)
				return false;
			return NullableMetadata.HasAnnotatablePositions(sig.RetType) ||
				sig.Params.Any(a => NullableMetadata.HasAnnotatablePositions(a)) ||
				method.GenericParameters.Any(a => NullableMetadata.HasAnnotatablePositions(a));
		}

		static ParamDef? GetParamDef(MethodDef method, int sequence) {
			foreach (var pd in method.ParamDefs) {
				if (pd.Sequence == sequence)
					return pd;
			}
			return null;
		}

		/// <summary>
		/// Gives a member from the compiled code the compiled context if the type's context is different
		/// </summary>
		void AddNullableContext(MethodDef method, byte compiledContext, byte targetContext) {
			if (compiledContext == targetContext || NullableMetadata.GetOwnContext(method) is not null || !HasAnnotatablePositions(method))
				return;
			AddNullableAttribute(method.CustomAttributes, NullableMetadata.NullableContextAttribute, compiledContext);
		}

		void AddNullable(IHasCustomAttribute member, TypeSig? type, byte compiledContext, byte targetContext) {
			if (compiledContext != targetContext)
				AddNullable(member.CustomAttributes, type, compiledContext);
		}

		void AddNullable(IList<CustomAttribute> attributes, TypeSig? type, byte context, bool isBaseType = false) {
			if (attributes.Any(a => a.TypeFullName == NullableMetadata.NullableAttribute) || !NullableMetadata.HasAnnotatablePositions(type, isBaseType))
				return;
			AddNullableAttribute(attributes, NullableMetadata.NullableAttribute, context);
		}

		void AddNullable(IList<GenericParam> genericParameters, byte context) {
			foreach (var gp in genericParameters) {
				if (NullableMetadata.CanBeAnnotated(gp) && !gp.CustomAttributes.Any(a => a.TypeFullName == NullableMetadata.NullableAttribute))
					AddNullableAttribute(gp.CustomAttributes, NullableMetadata.NullableAttribute, context);
				foreach (var gpc in gp.GenericParamConstraints)
					AddNullable(gpc.CustomAttributes, gpc.Constraint.ToTypeSig(), context);
			}
		}

		/// <summary>
		/// Replaces the nullable attributes of an edited method (and its parameters and generic parameters) with the
		/// original method's. The signatures are the same.
		/// </summary>
		void RestoreNullableAttributes(MethodDef editedMethod, MethodDef originalMethod) {
			ReplaceNullableAttributes(editedMethod.CustomAttributes, originalMethod);
			foreach (var pd in editedMethod.ParamDefs)
				RemoveAll(pd.CustomAttributes, NullableMetadata.IsNullableAttribute);
			foreach (var originalParamDef in originalMethod.ParamDefs) {
				if (!originalParamDef.CustomAttributes.Any(NullableMetadata.IsNullableAttribute))
					continue;
				var pd = GetParamDef(editedMethod, originalParamDef.Sequence);
				if (pd is null) {
					pd = targetModule.UpdateRowId(new ParamDefUser(originalParamDef.Sequence == 0 ? UTF8String.Empty : originalParamDef.Name, originalParamDef.Sequence));
					int index = 0;
					while (index < editedMethod.ParamDefs.Count && editedMethod.ParamDefs[index].Sequence < pd.Sequence)
						index++;
					editedMethod.ParamDefs.Insert(index, pd);
				}
				AddNullableAttributes(pd.CustomAttributes, originalParamDef, null);
			}
			ReplaceNullableAttributes(editedMethod.GenericParameters, originalMethod.GenericParameters);
		}

		static void ReplaceNullableAttributes(IList<GenericParam> genericParameters, IList<GenericParam> originalGenericParameters) {
			for (int i = 0; i < genericParameters.Count && i < originalGenericParameters.Count; i++) {
				var gp = genericParameters[i];
				var originalGp = originalGenericParameters[i];
				ReplaceNullableAttributes(gp.CustomAttributes, originalGp);
				for (int j = 0; j < gp.GenericParamConstraints.Count && j < originalGp.GenericParamConstraints.Count; j++)
					ReplaceNullableAttributes(gp.GenericParamConstraints[j].CustomAttributes, originalGp.GenericParamConstraints[j]);
			}
		}

		static void ReplaceNullableAttributes(IList<CustomAttribute> attributes, IHasCustomAttribute original, string? fullName = null) {
			RemoveAll(attributes, a => fullName is null ? NullableMetadata.IsNullableAttribute(a) : a.TypeFullName == fullName);
			AddNullableAttributes(attributes, original, fullName);
		}

		static void RemoveAll(IList<CustomAttribute> attributes, Func<CustomAttribute, bool> predicate) {
			for (int i = attributes.Count - 1; i >= 0; i--) {
				if (predicate(attributes[i]))
					attributes.RemoveAt(i);
			}
		}

		/// <summary>
		/// Adds copies of the nullable attributes of an original definition (their constructors are in the target module)
		/// </summary>
		static void AddNullableAttributes(IList<CustomAttribute> attributes, IHasCustomAttribute original, string? fullName) {
			foreach (var ca in original.CustomAttributes) {
				if (fullName is null ? NullableMetadata.IsNullableAttribute(ca) : ca.TypeFullName == fullName)
					attributes.Add(Clone(ca));
			}
		}

		static CustomAttribute Clone(CustomAttribute ca) {
			if (ca.IsRawBlob)
				return new CustomAttribute(ca.Constructor, ca.RawData);
			return new CustomAttribute(ca.Constructor, ca.ConstructorArguments.Select(Clone), ca.NamedArguments.Select(a => new CANamedArgument(a.IsField, a.Type, a.Name, Clone(a.Argument))));
		}

		static CAArgument Clone(CAArgument arg) => arg.Value is IList<CAArgument> args ?
			new CAArgument(arg.Type, args.Select(Clone).ToList()) :
			new CAArgument(arg.Type, arg.Value);

		/// <summary>
		/// Adds [Nullable(value)] or [NullableContext(value)]. The attribute constructor is the one the compiled code uses
		/// (the compiler's embedded type or a reference, e.g. to System.Private.CoreLib's public type), else the target
		/// module's. Nothing is added if neither module has one.
		/// </summary>
		void AddNullableAttribute(IList<CustomAttribute> attributes, string fullName, byte value) {
			if (!nullableAttributeCtors.TryGetValue(fullName, out var ctor)) {
				ctor = FindByteConstructor(sourceModule, fullName) switch {
					MethodDef md when oldMethodToNewMethod.TryGetValue(md, out var info) => info.TargetMember,
					MemberRef mr => Import((ICustomAttributeType)mr),
					_ => null,
				} ?? FindByteConstructor(targetModule, fullName);
				nullableAttributeCtors.Add(fullName, ctor);
			}
			if (ctor is not null)
				attributes.Add(new CustomAttribute(ctor, new[] { new CAArgument(targetModule.CorLibTypes.Byte, value) }));
		}
		readonly Dictionary<string, ICustomAttributeType?> nullableAttributeCtors = new Dictionary<string, ICustomAttributeType?>(StringComparer.Ordinal);

		static ICustomAttributeType? FindByteConstructor(ModuleDef module, string fullName) {
			if (module.Find(fullName, isReflectionName: false) is TypeDef type) {
				foreach (var method in type.Methods) {
					if (method.IsInstanceConstructor && IsByteConstructor(method.MethodSig))
						return method;
				}
				return null;
			}
			if (module is ModuleDefMD md) {
				uint rows = md.TablesStream.MemberRefTable.Rows;
				for (uint rid = 1; rid <= rows; rid++) {
					var mr = md.ResolveMemberRef(rid);
					if (mr is not null && mr.Name == ".ctor" && IsByteConstructor(mr.MethodSig) && mr.DeclaringType?.FullName == fullName)
						return mr;
				}
			}
			return null;
		}

		static bool IsByteConstructor(MethodSig? sig) =>
			sig is not null && sig.HasThis && sig.Params.Count == 1 && sig.Params[0].RemovePinnedAndModifiers().ElementType == ElementType.U1;
	}
}
