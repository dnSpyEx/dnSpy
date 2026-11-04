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
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler.TypeSystem;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;
using UpstreamField = ICSharpCode.Decompiler.TypeSystem.IField;
using UpstreamMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// Converts the upstream decompiler's symbols to the objects dnSpy uses as references (dnlib members, which
	/// the tooltip, navigation and highlighting code understands) and to dnSpy colors
	/// </summary>
	sealed class ReferenceFactory {
		readonly ModuleBridgeCache cache;
		readonly ModuleDef module;
		readonly TypeSigConverter typeSigConverter;

		public ModuleBridgeCache Cache => cache;
		public ModuleDef Module => module;
		public TypeSigConverter TypeSigConverter => typeSigConverter;

		/// <param name="module">The decompiled module</param>
		public ReferenceFactory(ModuleBridgeCache cache, ModuleDef module) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.module = module ?? throw new ArgumentNullException(nameof(module));
			typeSigConverter = new TypeSigConverter(cache, module);
		}

		static MetadataTextColorProvider ColorProvider => CSharpMetadataTextColorProvider.Instance;

		/// <summary>
		/// Gets the reference of a type (its definition or generic parameter) or null if it isn't available
		/// </summary>
		public object? GetTypeReference(IType type) {
			if (type is ITypeParameter tp)
				return GetGenericParam(tp);
			var def = type.GetDefinition();
			if (def is not null)
				return cache.ToDnlib(def) as TypeDef;
			return null;
		}

		GenericParam? GetGenericParam(ITypeParameter tp) {
			if (tp.Owner is IEntity owner && cache.ToDnlib(owner) is ITypeOrMethodDef dnlibOwner) {
				foreach (var gp in dnlibOwner.GenericParameters) {
					if (gp.Number == tp.Index)
						return gp;
				}
			}
			return null;
		}

		/// <summary>
		/// Gets the reference of a member or null if it isn't available. Members of generic instances
		/// (eg. List&lt;int&gt;.Add) become dnlib member references so tooltips show the instantiated signature.
		/// </summary>
		public object? GetMemberReference(IMember member) {
			member = GetExtensionImplementation(member) ?? member;
			var definition = cache.ToDnlib(member.MemberDefinition ?? member) as IMemberDef;
			if (definition is null)
				return null;
			try {
				switch (member) {
				case UpstreamMethod method when definition is MethodDef md:
					return GetMethodReference(method, md);
				case UpstreamField field when definition is FieldDef fd && IsSpecializedType(field.DeclaringType):
					return new MemberRefUser(fd.Module, fd.Name, fd.FieldSig, new TypeSpecUser(typeSigConverter.ToTypeSig(field.DeclaringType)));
				}
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException)) {
				// Unusual (eg. obfuscated) signatures: fall back to the definition
			}
			return definition;
		}

		/// <summary>
		/// A member of a C# 14 extension block is a metadata-only member of a nested type, its code is in the
		/// implementation method in the static class (the method the tree view shows and the debugger stops in). An
		/// extension property's is its getter's (else setter's).
		/// </summary>
		static IMember? GetExtensionImplementation(IMember member) {
			var method = member switch {
				UpstreamMethod m => m,
				IProperty p => p.Getter ?? p.Setter,
				_ => null,
			};
			if (method is null || method.MemberDefinition is not UpstreamMethod methodDefinition)
				return null;
			try {
				if (methodDefinition.ResolveExtensionInfo()?.InfoOfExtensionMember(methodDefinition) is ExtensionMemberInfo info)
					return info.ImplementationMethod;
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException)) {
			}
			return null;
		}

		object GetMethodReference(UpstreamMethod method, MethodDef md) {
			IMethodDefOrRef methodRef = md;
			if (IsSpecializedType(method.DeclaringType) && md.MethodSig is MethodSig sig)
				methodRef = new MemberRefUser(md.Module, md.Name, sig, new TypeSpecUser(typeSigConverter.ToTypeSig(method.DeclaringType)));
			if (method.TypeArguments.Count > 0 && !IsIdentity(method.TypeArguments, SymbolKind.Method))
				return new MethodSpecUser(methodRef, new GenericInstMethodSig(method.TypeArguments.Select(typeSigConverter.ToTypeSig).ToList()));
			return methodRef;
		}

		static bool IsSpecializedType(IType? declaringType) =>
			declaringType is ParameterizedType pt && !IsIdentity(pt.TypeArguments, SymbolKind.TypeDefinition);

		static bool IsIdentity(System.Collections.Generic.IReadOnlyList<IType> typeArguments, SymbolKind ownerKind) {
			for (int i = 0; i < typeArguments.Count; i++) {
				if (!(typeArguments[i] is ITypeParameter tp) || tp.Index != i || tp.OwnerType != ownerKind)
					return false;
			}
			return true;
		}

		/// <summary>
		/// Gets the color of a reference, or if there's no reference, the color of the upstream symbol
		/// </summary>
		public object GetColor(object? reference, object? symbol) {
			// An extension property refers to its getter's implementation method (see GetExtensionImplementation), but
			// it's still a property
			if (symbol is IProperty property && GetExtensionImplementation(property) is not null)
				return GetColor(symbol);
			if (reference is not null) {
				var color = ColorProvider.GetColor(reference);
				if (!BoxedTextColor.Text.Equals(color))
					return color;
			}
			return GetColor(symbol);
		}

		public static object GetColor(object? symbol) {
			switch (symbol) {
			case ITypeParameter tp:
				return tp.OwnerType == SymbolKind.Method ? BoxedTextColor.MethodGenericParameter : BoxedTextColor.TypeGenericParameter;
			case IType type:
				return GetTypeColor(type);
			case UpstreamMethod method:
				if (method.IsConstructor && method.DeclaringType is IType ctorType)
					return GetTypeColor(ctorType);
				if (method.IsExtensionMethod)
					return BoxedTextColor.ExtensionMethod;
				return method.IsStatic ? BoxedTextColor.StaticMethod : BoxedTextColor.InstanceMethod;
			case UpstreamField field:
				if (field.DeclaringType?.Kind == TypeKind.Enum)
					return BoxedTextColor.EnumField;
				if (field.IsConst)
					return BoxedTextColor.LiteralField;
				return field.IsStatic ? BoxedTextColor.StaticField : BoxedTextColor.InstanceField;
			case IProperty property:
				return property.IsStatic ? BoxedTextColor.StaticProperty : BoxedTextColor.InstanceProperty;
			case IEvent ev:
				return ev.IsStatic ? BoxedTextColor.StaticEvent : BoxedTextColor.InstanceEvent;
			case INamespace:
				return BoxedTextColor.Namespace;
			}
			return BoxedTextColor.Text;
		}

		static object GetTypeColor(IType type) {
			switch (type.Kind) {
			case TypeKind.Interface:
				return BoxedTextColor.Interface;
			case TypeKind.Struct:
			case TypeKind.Void:
				return BoxedTextColor.ValueType;
			case TypeKind.Enum:
				return BoxedTextColor.Enum;
			case TypeKind.Delegate:
				return BoxedTextColor.Delegate;
			case TypeKind.TypeParameter:
				return BoxedTextColor.TypeGenericParameter;
			case TypeKind.Class:
				if (type.GetDefinition() is ITypeDefinition def) {
					if (def.IsStatic)
						return BoxedTextColor.StaticType;
					if (def.IsSealed)
						return BoxedTextColor.SealedType;
				}
				return BoxedTextColor.Type;
			}
			return BoxedTextColor.Type;
		}
	}
}
