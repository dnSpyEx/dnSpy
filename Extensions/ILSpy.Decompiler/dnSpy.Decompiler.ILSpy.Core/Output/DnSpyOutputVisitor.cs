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

using dnSpy.Decompiler.ILSpy.Core.Transforms;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// Upstream's C# output, and the syntax upstream 11.2 can't write: C# 14 compound assignment operator declarations
	/// (<see cref="CompoundAssignmentOperatorDeclarationTransform"/>)
	/// </summary>
	sealed class DnSpyOutputVisitor : CSharpOutputVisitor {
		public DnSpyOutputVisitor(TokenWriter writer, CSharpFormattingOptions formattingPolicy) : base(writer, formattingPolicy) { }

		/// <summary>
		/// Upstream 11.2 can leave an 'in' argument without its expression in the tree: a C# 12 inline array argument
		/// (e.g. 'Path.Combine(a, b, c, d, e)' on .NET 10 passes a ReadOnlySpan of an InlineArray5) whose inline array
		/// type can't be resolved (a missing reference). Upstream's output throws a NullReferenceException and the
		/// whole type fails to decompile; this writes a comment in its place. A no-op on correct upstream output.
		/// </summary>
		public override void VisitDirectionExpression(DirectionExpression directionExpression) {
			if (directionExpression.Expression is not null) {
				base.VisitDirectionExpression(directionExpression);
				return;
			}
			StartNode(directionExpression);
			WriteKeyword(directionExpression.FieldDirection switch {
				FieldDirection.Out => DirectionExpression.OutKeyword,
				FieldDirection.Ref => DirectionExpression.RefKeyword,
				_ => DirectionExpression.InKeyword,
			});
			Space();
			writer.WriteComment(CommentType.MultiLine, " unknown argument (unresolved reference) ");
			EndNode(directionExpression);
		}

		/// <summary>
		/// C# 12 collection expressions ('[a, b, ..c]', <see cref="CollectionExpressionTransform"/>), which upstream
		/// 11.2 doesn't have
		/// </summary>
		public override void VisitArrayInitializerExpression(ArrayInitializerExpression arrayInitializerExpression) {
			if (arrayInitializerExpression.Annotation<CollectionExpressionAnnotation>() is null) {
				base.VisitArrayInitializerExpression(arrayInitializerExpression);
				return;
			}
			StartNode(arrayInitializerExpression);
			WriteCommaSeparatedListInBrackets(arrayInitializerExpression.Elements);
			EndNode(arrayInitializerExpression);
		}

		public override void VisitMethodDeclaration(MethodDeclaration methodDeclaration) {
			if (methodDeclaration.Annotation<CompoundAssignmentOperatorAnnotation>() is not CompoundAssignmentOperatorAnnotation op) {
				base.VisitMethodDeclaration(methodDeclaration);
				return;
			}
			// Same as VisitOperatorDeclaration: 'public void operator checked +=(int value)'
			StartNode(methodDeclaration);
			WriteAttributes(methodDeclaration.Attributes);
			WriteModifiers(methodDeclaration.Modifiers);
			methodDeclaration.ReturnType?.AcceptVisitor(this);
			Space();
			WritePrivateImplementationType(methodDeclaration.PrivateImplementationType);
			WriteKeyword(OperatorDeclaration.OperatorKeyword);
			Space();
			if (op.IsChecked) {
				WriteKeyword(OperatorDeclaration.CheckedKeyword);
				Space();
			}
			WriteToken(op.Token);
			Space(policy.SpaceBeforeMethodDeclarationParentheses);
			WriteCommaSeparatedListInParenthesis(methodDeclaration.Parameters, policy.SpaceWithinMethodDeclarationParentheses);
			WriteMethodBody(methodDeclaration.Body, policy.MethodBraceStyle);
			EndNode(methodDeclaration);
		}
	}
}
