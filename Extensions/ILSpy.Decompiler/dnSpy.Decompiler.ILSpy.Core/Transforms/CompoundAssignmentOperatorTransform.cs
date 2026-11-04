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
using System.Reflection;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Transforms {
	/// <summary>
	/// C# 14 user-defined compound assignment operators ('public void operator +=(int n)'): upstream 11.2 doesn't know
	/// them and shows a use as a call ('counter.op_AdditionAssignment(5);'), which the compiler rejects for an operator
	/// of a referenced assembly. This turns such statements back into 'counter += 5;' / 'counter++;'. A no-op if there
	/// are no such calls.
	/// </summary>
	sealed class CompoundAssignmentOperatorTransform : IAstTransform {
		static readonly Dictionary<string, AssignmentOperatorType> assignmentOperators = new Dictionary<string, AssignmentOperatorType>(StringComparer.Ordinal) {
			{ "op_AdditionAssignment", AssignmentOperatorType.Add },
			{ "op_SubtractionAssignment", AssignmentOperatorType.Subtract },
			{ "op_MultiplicationAssignment", AssignmentOperatorType.Multiply },
			{ "op_DivisionAssignment", AssignmentOperatorType.Divide },
			{ "op_ModulusAssignment", AssignmentOperatorType.Modulus },
			{ "op_BitwiseAndAssignment", AssignmentOperatorType.BitwiseAnd },
			{ "op_BitwiseOrAssignment", AssignmentOperatorType.BitwiseOr },
			{ "op_ExclusiveOrAssignment", AssignmentOperatorType.ExclusiveOr },
			{ "op_LeftShiftAssignment", AssignmentOperatorType.ShiftLeft },
			{ "op_RightShiftAssignment", AssignmentOperatorType.ShiftRight },
			{ "op_UnsignedRightShiftAssignment", AssignmentOperatorType.UnsignedShiftRight },
		};

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var statement in rootNode.Descendants.OfType<ExpressionStatement>().ToArray()) {
				if (statement.Expression is not InvocationExpression invocation || invocation.Target is not MemberReferenceExpression memberRef)
					continue;
				if (invocation.GetSymbol() is not IMethod method || method.IsStatic || method.ReturnType.Kind != TypeKind.Void || !IsSpecialName(method))
					continue;
				if (memberRef.Target is BaseReferenceExpression || memberRef.TypeArguments.Count != 0)
					continue;
				Expression? replacement = null;
				if (assignmentOperators.TryGetValue(method.Name, out var op) && method.Parameters.Count == 1 && invocation.Arguments.Count == 1) {
					if (invocation.Arguments.Single() is NamedArgumentExpression)
						continue;
					var target = memberRef.Target.Detach();
					var value = invocation.Arguments.Single().Detach();
					replacement = new AssignmentExpression(target, op, value);
				}
				else if ((method.Name == "op_IncrementAssignment" || method.Name == "op_DecrementAssignment") && method.Parameters.Count == 0 && invocation.Arguments.Count == 0) {
					var target = memberRef.Target.Detach();
					replacement = new UnaryOperatorExpression(method.Name == "op_IncrementAssignment" ? UnaryOperatorType.PostIncrement : UnaryOperatorType.PostDecrement, target);
				}
				if (replacement is null)
					continue;
				replacement.CopyAnnotationsFrom(invocation);
				statement.Expression = replacement;
			}
		}

		// (11.2 doesn't know these operator names, so the method isn't an operator in its type system)
		internal static bool IsSpecialName(IMethod method) {
			var definition = method.MemberDefinition as IMethod ?? method;
			if (definition.ParentModule?.MetadataFile is not MetadataFile file || definition.MetadataToken.Kind != HandleKind.MethodDefinition)
				return false;
			try {
				return (file.Metadata.GetMethodDefinition((MethodDefinitionHandle)definition.MetadataToken).Attributes & MethodAttributes.SpecialName) != 0;
			}
			catch (BadImageFormatException) {
				return false;
			}
		}
	}
}
