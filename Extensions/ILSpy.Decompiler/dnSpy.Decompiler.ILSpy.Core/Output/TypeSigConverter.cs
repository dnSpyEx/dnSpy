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
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler.TypeSystem;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// Converts upstream types to dnlib type signatures. They're only used for display (tooltips, references
	/// to generic instances), never written to a module.
	/// </summary>
	sealed class TypeSigConverter {
		const int MaxDepth = 64;
		readonly ModuleBridgeCache cache;
		readonly ModuleDef module;
		int depth;

		/// <param name="module">Module whose corlib types are used for primitive types</param>
		public TypeSigConverter(ModuleBridgeCache cache, ModuleDef module) {
			this.cache = cache ?? throw new ArgumentNullException(nameof(cache));
			this.module = module ?? throw new ArgumentNullException(nameof(module));
		}

		public TypeSig ToTypeSig(IType? type) {
			if (type is null || depth >= MaxDepth)
				return module.CorLibTypes.Object;
			depth++;
			try {
				return Convert(type);
			}
			finally {
				depth--;
			}
		}

		TypeSig Convert(IType type) {
			switch (type) {
			case ITypeDefinition def:
				return FromDefinition(def);

			case ParameterizedType pt: {
				var genericType = FromGenericType(pt.GenericType);
				ClassOrValueTypeSig genericTypeSig = IsValueType(pt.GenericType, genericType) ? new ValueTypeSig(genericType) : new ClassSig(genericType);
				return new GenericInstSig(genericTypeSig, pt.TypeArguments.Select(ToTypeSig).ToList());
			}

			case ArrayType at:
				if (at.Dimensions <= 1)
					return new SZArraySig(ToTypeSig(at.ElementType));
				return new ArraySig(ToTypeSig(at.ElementType), (uint)at.Dimensions);

			case PointerType ptr:
				return new PtrSig(ToTypeSig(ptr.ElementType));

			case ByReferenceType br:
				return new ByRefSig(ToTypeSig(br.ElementType));

			case ITypeParameter tp: {
				// The owner gives the generic parameter its name when it's formatted
				var owner = tp.Owner is IEntity ownerEntity ? cache.ToDnlib(ownerEntity) : null;
				if (tp.OwnerType == ICSharpCode.Decompiler.TypeSystem.SymbolKind.Method)
					return new GenericMVar((uint)Math.Max(0, tp.Index), owner as MethodDef);
				return new GenericVar((uint)Math.Max(0, tp.Index), owner as TypeDef);
			}

			case TupleType tuple:
				return ToTypeSig(tuple.UnderlyingType);

			case ICSharpCode.Decompiler.TypeSystem.Implementation.ModifiedType modified: {
				var modifier = FromTypeDefOrRef(modified.Modifier);
				var element = ToTypeSig(modified.ElementType);
				if (modifier is null)
					return element;
				return modified.Kind == TypeKind.ModReq ? new CModReqdSig(modifier, element) : new CModOptSig(modifier, element);
			}

			case FunctionPointerType fnPtr: {
				var sig = MethodSig.CreateStatic(ToTypeSig(fnPtr.ReturnType), fnPtr.ParameterTypes.Select(ToTypeSig).ToArray());
				return new FnPtrSig(sig);
			}
			}

			switch (type.Kind) {
			case TypeKind.Void:
				return module.CorLibTypes.Void;
			case TypeKind.NInt:
				return module.CorLibTypes.IntPtr;
			case TypeKind.NUInt:
				return module.CorLibTypes.UIntPtr;
			case TypeKind.Dynamic:
			case TypeKind.Null:
			case TypeKind.None:
				return module.CorLibTypes.Object;
			}

			// Nullability wrappers and other decorated types
			if (type.GetDefinition() is ITypeDefinition definition && type.TypeArguments.Count == 0)
				return FromDefinition(definition);
			var unresolved = CreateTypeRef(type);
			return type.IsReferenceType == false ? new ValueTypeSig(unresolved) : new ClassSig(unresolved);
		}

		TypeSig FromDefinition(ITypeDefinition def) {
			var corLibType = GetCorLibType(def.KnownTypeCode);
			if (corLibType is not null)
				return corLibType;
			var typeDefOrRef = FromTypeDefOrRef(def) ?? CreateTypeRef(def);
			return IsValueType(def, typeDefOrRef) ? new ValueTypeSig(typeDefOrRef) : new ClassSig(typeDefOrRef);
		}

		static bool IsValueType(IType type, ITypeDefOrRef typeDefOrRef) {
			if (typeDefOrRef is TypeDef td)
				return td.IsValueType;
			return type.IsReferenceType == false;
		}

		ITypeDefOrRef FromGenericType(IType genericType) => FromTypeDefOrRef(genericType) ?? CreateTypeRef(genericType);

		/// <summary>
		/// Gets the dnlib type definition of a (non-generic-instance) type, or null if it isn't defined in a bridged module
		/// </summary>
		ITypeDefOrRef? FromTypeDefOrRef(IType type) {
			var def = type.GetDefinition();
			if (def is null)
				return null;
			return cache.ToDnlib(def) as TypeDef;
		}

		/// <summary>
		/// A type whose definition isn't available (e.g. its assembly couldn't be resolved)
		/// </summary>
		ITypeDefOrRef CreateTypeRef(IType type) {
			var name = type.Name;
			if (type.TypeParameterCount > 0)
				name += "`" + type.TypeParameterCount.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (type.DeclaringType is IType declaringType) {
				var declaringRef = FromGenericType(declaringType.GetDefinition() ?? declaringType) as IResolutionScope;
				return new TypeRefUser(module, string.Empty, name, declaringRef);
			}
			return new TypeRefUser(module, type.Namespace, name);
		}

		CorLibTypeSig? GetCorLibType(KnownTypeCode code) =>
			code switch {
				KnownTypeCode.Object => module.CorLibTypes.Object,
				KnownTypeCode.String => module.CorLibTypes.String,
				KnownTypeCode.Boolean => module.CorLibTypes.Boolean,
				KnownTypeCode.Char => module.CorLibTypes.Char,
				KnownTypeCode.SByte => module.CorLibTypes.SByte,
				KnownTypeCode.Byte => module.CorLibTypes.Byte,
				KnownTypeCode.Int16 => module.CorLibTypes.Int16,
				KnownTypeCode.UInt16 => module.CorLibTypes.UInt16,
				KnownTypeCode.Int32 => module.CorLibTypes.Int32,
				KnownTypeCode.UInt32 => module.CorLibTypes.UInt32,
				KnownTypeCode.Int64 => module.CorLibTypes.Int64,
				KnownTypeCode.UInt64 => module.CorLibTypes.UInt64,
				KnownTypeCode.Single => module.CorLibTypes.Single,
				KnownTypeCode.Double => module.CorLibTypes.Double,
				KnownTypeCode.IntPtr => module.CorLibTypes.IntPtr,
				KnownTypeCode.UIntPtr => module.CorLibTypes.UIntPtr,
				KnownTypeCode.Void => module.CorLibTypes.Void,
				KnownTypeCode.TypedReference => module.CorLibTypes.TypedReference,
				_ => null,
			};
	}
}
