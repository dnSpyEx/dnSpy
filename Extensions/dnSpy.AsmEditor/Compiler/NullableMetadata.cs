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

using System.Collections.Generic;
using dnlib.DotNet;

namespace dnSpy.AsmEditor.Compiler {
	/// <summary>
	/// Reads the C# compiler's nullable reference type metadata. A type in a signature has one byte per position (0 =
	/// oblivious, 1 = not annotated, 2 = annotated). [Nullable] on the signature's owner has the bytes (one byte if they're
	/// all the same), else they're all the value of the nearest [NullableContext] (method, then declaring types), else 0.
	/// </summary>
	static class NullableMetadata {
		public const string NullableAttribute = "System.Runtime.CompilerServices.NullableAttribute";
		public const string NullableContextAttribute = "System.Runtime.CompilerServices.NullableContextAttribute";

		public static bool IsNullableAttribute(CustomAttribute ca) {
			var fullName = ca.TypeFullName;
			return fullName == NullableAttribute || fullName == NullableContextAttribute;
		}

		/// <summary>
		/// The value of the [NullableContext] of <paramref name="owner"/> (a type or a method), or null if it has none
		/// </summary>
		public static byte? GetOwnContext(IHasCustomAttribute owner) {
			var ca = owner.CustomAttributes.Find(NullableContextAttribute);
			if (ca is not null && ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is byte value)
				return value;
			return null;
		}

		/// <summary>
		/// The context of the members of <paramref name="type"/>: its [NullableContext] or its declaring types'
		/// </summary>
		public static byte GetContext(TypeDef type) {
			int count = 0;
			for (var t = type; t is not null && count < 100; t = t.DeclaringType, count++) {
				if (GetOwnContext(t) is byte value)
					return value;
			}
			return 0;
		}

		/// <summary>
		/// The bytes of the [Nullable] of <paramref name="owner"/>: an array, one byte, or null if it has none
		/// </summary>
		static object? GetNullableValue(IHasCustomAttribute? owner) {
			if (owner is null)
				return null;
			var ca = owner.CustomAttributes.Find(NullableAttribute);
			if (ca is null || ca.ConstructorArguments.Count != 1)
				return null;
			var value = ca.ConstructorArguments[0].Value;
			if (value is byte)
				return value;
			if (value is IList<CAArgument> args) {
				var bytes = new byte[args.Count];
				for (int i = 0; i < bytes.Length; i++) {
					if (args[i].Value is not byte b)
						return null;
					bytes[i] = b;
				}
				return bytes;
			}
			return null;
		}

		/// <summary>
		/// The positions of a type in a [Nullable] byte array: true if it's a reference type, an array or a type
		/// parameter, false if it's a generic value type (its byte is a placeholder). Non-generic value types have no
		/// position, a nullable value type has its type argument's. Returns null if the type isn't supported.
		/// </summary>
		public static List<bool>? GetPositions(TypeSig? type) {
			var positions = new List<bool>();
			return AddPositions(type, positions, 0) ? positions : null;
		}

		static bool AddPositions(TypeSig? type, List<bool> positions, int depth) {
			if (type is null || depth > 100)
				return false;
			type = type.RemovePinnedAndModifiers();
			switch (type.ElementType) {
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
			case ElementType.I:
			case ElementType.U:
			case ElementType.TypedByRef:
			case ElementType.ValueType:
				return true;

			case ElementType.String:
			case ElementType.Object:
			case ElementType.Class:
			case ElementType.Var:
			case ElementType.MVar:
				positions.Add(true);
				return true;

			case ElementType.SZArray:
			case ElementType.Array:
				positions.Add(true);
				return AddPositions(type.Next, positions, depth + 1);

			case ElementType.ByRef:
			case ElementType.Ptr:
				return AddPositions(type.Next, positions, depth + 1);

			case ElementType.GenericInst:
				var gis = (GenericInstSig)type;
				if (gis.GenericType is ValueTypeSig valueType) {
					if (gis.GenericArguments.Count == 1 && valueType.TypeDefOrRef?.FullName == "System.Nullable`1")
						return AddPositions(gis.GenericArguments[0], positions, depth + 1);
					positions.Add(false);
				}
				else if (gis.GenericType is ClassSig)
					positions.Add(true);
				else
					return false;
				foreach (var arg in gis.GenericArguments) {
					if (!AddPositions(arg, positions, depth + 1))
						return false;
				}
				return true;

			default:
				return false;
			}
		}

		/// <summary>
		/// Whether the type has a position that can be annotated
		/// </summary>
		/// <param name="type">Type</param>
		/// <param name="isBaseType">The type is a base type or an interface of a type: only its type arguments can be
		/// annotated (the first position is ignored)</param>
		public static bool HasAnnotatablePositions(TypeSig? type, bool isBaseType = false) {
			var positions = GetPositions(type);
			if (positions is null)
				return false;
			for (int i = isBaseType ? 1 : 0; i < positions.Count; i++) {
				if (positions[i])
					return true;
			}
			return false;
		}

		/// <summary>
		/// Whether a reference type in <paramref name="type"/> is oblivious (code compiled with nullable annotations
		/// disabled). False if it can't be determined.
		/// </summary>
		/// <param name="type">Type</param>
		/// <param name="owner">Owner of the type's [Nullable] (field, property, event, parameter, ...)</param>
		/// <param name="context">The owner's nullable context</param>
		/// <param name="isBaseType">The type is a base type or an interface of a type: only its type arguments can be
		/// annotated (the first position is ignored)</param>
		public static bool HasObliviousPositions(TypeSig? type, IHasCustomAttribute? owner, byte context, bool isBaseType = false) {
			var positions = GetPositions(type);
			if (positions is null)
				return false;
			int first = isBaseType ? 1 : 0;
			bool annotatable = false;
			for (int i = first; i < positions.Count; i++)
				annotatable |= positions[i];
			if (!annotatable)
				return false;
			switch (GetNullableValue(owner)) {
			case null:
				return context == 0;
			case byte value:
				return value == 0;
			case byte[] values:
				if (values.Length != positions.Count)
					return false;
				for (int i = first; i < values.Length; i++) {
					if (positions[i] && values[i] == 0)
						return true;
				}
				return false;
			default:
				return false;
			}
		}

		/// <summary>
		/// Whether a reference type in <paramref name="type"/> is annotated or not annotated (not oblivious)
		/// </summary>
		/// <param name="type">Type</param>
		/// <param name="owner">Owner of the type's [Nullable] (field, property, event, parameter, ...)</param>
		/// <param name="context">The owner's nullable context</param>
		/// <param name="isBaseType">The type is a base type or an interface of a type (the first position is ignored)</param>
		public static bool HasAnnotations(TypeSig? type, IHasCustomAttribute? owner, byte context, bool isBaseType = false) {
			var positions = GetPositions(type);
			if (positions is null)
				return true;
			int first = isBaseType ? 1 : 0;
			bool annotatable = false;
			for (int i = first; i < positions.Count; i++)
				annotatable |= positions[i];
			if (!annotatable)
				return false;
			switch (GetNullableValue(owner)) {
			case null:
				return context != 0;
			case byte value:
				return value != 0;
			case byte[] values:
				if (values.Length != positions.Count)
					return true;
				for (int i = first; i < values.Length; i++) {
					if (positions[i] && values[i] != 0)
						return true;
				}
				return false;
			default:
				return true;
			}
		}

		public static bool HasAnnotations(GenericParam gp, byte context) {
			if (CanBeAnnotated(gp)) {
				var value = GetNullableValue(gp);
				if ((value is byte b ? b : value is null ? context : (byte)1) != 0)
					return true;
			}
			foreach (var gpc in gp.GenericParamConstraints) {
				if (HasAnnotations(gpc.Constraint.ToTypeSig(), gpc, context))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Whether a generic parameter's nullability (e.g. an unconstrained T, 'class' or 'class?') or one of its
		/// constraints is oblivious
		/// </summary>
		public static bool HasObliviousPositions(GenericParam gp, byte context) {
			if (CanBeAnnotated(gp)) {
				var value = GetNullableValue(gp);
				if ((value is byte b ? b : value is null ? context : (byte)1) == 0)
					return true;
			}
			foreach (var gpc in gp.GenericParamConstraints) {
				if (HasObliviousPositions(gpc.Constraint.ToTypeSig(), gpc, context))
					return true;
			}
			return false;
		}

		/// <summary>
		/// Whether a generic parameter has a nullability (not a 'struct' or 'unmanaged' constraint)
		/// </summary>
		public static bool CanBeAnnotated(GenericParam gp) => !gp.HasNotNullableValueTypeConstraint;

		public static bool HasAnnotatablePositions(GenericParam gp) {
			if (CanBeAnnotated(gp))
				return true;
			foreach (var gpc in gp.GenericParamConstraints) {
				if (HasAnnotatablePositions(gpc.Constraint.ToTypeSig()))
					return true;
			}
			return false;
		}
	}
}
