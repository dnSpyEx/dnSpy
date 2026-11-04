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
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Transforms {
	/// <summary>
	/// C# 14 user-defined compound assignment and instance increment/decrement operators ('public void operator +=(int n)',
	/// 'public void operator ++()'): upstream 11.2 doesn't know them and declares them as methods
	/// ('[CompilerFeatureRequired("UserDefinedCompoundAssignmentOperators")] public void op_AdditionAssignment(int n)'),
	/// which doesn't compile and doesn't match the uses (<see cref="CompoundAssignmentOperatorTransform"/>). This marks
	/// the method so it's written as an operator (<see cref="Output.DnSpyOutputVisitor"/>) and removes the attribute
	/// the compiler adds. A no-op once upstream declares them as operators (there's no such method declaration then).
	/// </summary>
	sealed class CompoundAssignmentOperatorDeclarationTransform : IAstTransform {
		static readonly Dictionary<string, (string token, bool isChecked, int parameters)> operators = new Dictionary<string, (string, bool, int)>(StringComparer.Ordinal) {
			{ "op_AdditionAssignment", ("+=", false, 1) },
			{ "op_SubtractionAssignment", ("-=", false, 1) },
			{ "op_MultiplicationAssignment", ("*=", false, 1) },
			{ "op_DivisionAssignment", ("/=", false, 1) },
			{ "op_ModulusAssignment", ("%=", false, 1) },
			{ "op_BitwiseAndAssignment", ("&=", false, 1) },
			{ "op_BitwiseOrAssignment", ("|=", false, 1) },
			{ "op_ExclusiveOrAssignment", ("^=", false, 1) },
			{ "op_LeftShiftAssignment", ("<<=", false, 1) },
			{ "op_RightShiftAssignment", (">>=", false, 1) },
			{ "op_UnsignedRightShiftAssignment", (">>>=", false, 1) },
			{ "op_CheckedAdditionAssignment", ("+=", true, 1) },
			{ "op_CheckedSubtractionAssignment", ("-=", true, 1) },
			{ "op_CheckedMultiplicationAssignment", ("*=", true, 1) },
			{ "op_CheckedDivisionAssignment", ("/=", true, 1) },
			{ "op_IncrementAssignment", ("++", false, 0) },
			{ "op_DecrementAssignment", ("--", false, 0) },
			{ "op_CheckedIncrementAssignment", ("++", true, 0) },
			{ "op_CheckedDecrementAssignment", ("--", true, 0) },
		};

		const string CompilerFeatureRequiredAttribute = "System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute";
		const string FeatureName = "UserDefinedCompoundAssignmentOperators";
		const string SpecialNameAttribute = "System.Runtime.CompilerServices.SpecialNameAttribute";

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var method in rootNode.Descendants.OfType<MethodDeclaration>()) {
				if (method.GetSymbol() is not IMethod symbol || symbol.IsStatic || symbol.ReturnType.Kind != TypeKind.Void)
					continue;
				if (!operators.TryGetValue(symbol.Name, out var info) || symbol.Parameters.Count != info.parameters || method.TypeParameters.Count != 0)
					continue;
				if (!CompoundAssignmentOperatorTransform.IsSpecialName(symbol))
					continue;
				RemoveFeatureAttribute(method);
				method.AddAnnotation(new CompoundAssignmentOperatorAnnotation(info.token, info.isChecked));
			}
		}

		// The compiler adds both to an operator ([SpecialName] is the method's specialname flag)
		static void RemoveFeatureAttribute(EntityDeclaration declaration) {
			foreach (var section in declaration.Attributes.ToArray()) {
				foreach (var attribute in section.Attributes.ToArray()) {
					if (attribute.Type.GetSymbol() is not IType type)
						continue;
					if ((type.FullName == CompilerFeatureRequiredAttribute && attribute.Arguments.FirstOrDefault() is PrimitiveExpression { Value: FeatureName }) ||
						(type.FullName == SpecialNameAttribute && attribute.Arguments.Count == 0))
						attribute.Remove();
				}
				if (section.Attributes.Count == 0)
					section.Remove();
			}
		}
	}

	/// <summary>
	/// A method declaration that is written as a C# 14 compound assignment (or instance increment/decrement) operator
	/// </summary>
	sealed class CompoundAssignmentOperatorAnnotation {
		public string Token { get; }
		public bool IsChecked { get; }
		public CompoundAssignmentOperatorAnnotation(string token, bool isChecked) {
			Token = token;
			IsChecked = isChecked;
		}
	}
}
