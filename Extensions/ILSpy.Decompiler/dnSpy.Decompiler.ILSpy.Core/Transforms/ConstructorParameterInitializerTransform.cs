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
using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Transforms {
	/// <summary>
	/// Upstream 11.2 can move a constructor's first assignments to field and property initializers when they read a
	/// constructor parameter, without making the constructor a primary constructor. It checks for parameters only in
	/// the IL of the assigned expression itself, which C# conversions don't have. Example (Iced): the constructor
	/// 'MvexInfo(Code code) { index = (int)(code - 4611); }' became 'private readonly int index = (int)(code - 4611);'
	/// and an empty constructor. That doesn't compile, and the constructor looks like it does nothing.
	/// This moves such initializers back to the start of the constructor. A no-op on correct upstream output: outside
	/// a primary constructor, an initializer can't read a constructor's parameters.
	/// </summary>
	sealed class ConstructorParameterInitializerTransform : IAstTransform {
		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var type in rootNode.Descendants.OfType<TypeDeclaration>().ToArray())
				Run(type);
		}

		static void Run(TypeDeclaration type) {
			if (type.HasPrimaryConstructor || type.PrimaryConstructorParameters.Count != 0)
				return;
			// Upstream only moves the assignments of a constructor that doesn't call this(...), and when there are
			// several, only those they all have. Only the case with one is handled (its parameters are the ones read).
			var ctors = type.Members.OfType<ConstructorDeclaration>().Where(a =>
				!a.HasModifier(Modifiers.Static) && a.Body is not null && a.Initializer?.ConstructorInitializerType != ConstructorInitializerType.This).ToArray();
			if (ctors.Length != 1)
				return;
			var ctor = ctors[0];
			var body = ctor.Body!;
			var function = ctor.Annotation<ILFunction>();
			if (function is null || ctor.GetSymbol() is not IMethod ctorMethod)
				return;
			var thisVariable = function.Variables.FirstOrDefault(a => a.Kind == VariableKind.Parameter && a.Index == -1);
			if (thisVariable is null)
				return;

			var assignments = new List<Statement>();
			foreach (var member in type.Members) {
				if (member.HasModifier(Modifiers.Static) || member.HasModifier(Modifiers.Const))
					continue;
				switch (member) {
				case FieldDeclaration field:
					foreach (var variable in field.Variables) {
						if (variable.Initializer is not Expression initializer || !ReadsParameter(initializer, ctorMethod) || GetField(field, variable) is not IField fieldSymbol)
							continue;
						assignments.Add(CreateAssignment(ctor, thisVariable, fieldSymbol, initializer.Detach()));
					}
					break;

				case PropertyDeclaration property:
					if (property.Initializer is not Expression propertyInitializer || !ReadsParameter(propertyInitializer, ctorMethod) || property.GetSymbol() is not IProperty propertySymbol)
						continue;
					assignments.Add(CreateAssignment(ctor, thisVariable, propertySymbol, propertyInitializer.Detach()));
					break;
				}
			}
			if (assignments.Count == 0)
				return;
			var first = body.Statements.FirstOrDefault();
			foreach (var assignment in assignments) {
				if (first is null)
					body.Statements.Add(assignment);
				else
					body.Statements.InsertBefore(first, assignment);
			}
		}

		// The symbol is the declaration's (upstream declares one field per declaration)
		static IField? GetField(FieldDeclaration declaration, VariableInitializer variable) =>
			variable.GetSymbol() as IField ?? (declaration.Variables.Count == 1 ? declaration.GetSymbol() as IField : null);

		// (The variable's function isn't the constructor's ILFunction annotation, it's another one of the same method)
		static bool ReadsParameter(Expression initializer, IMethod ctor) {
			foreach (var node in initializer.DescendantsAndSelf) {
				if (node is IdentifierExpression && node.GetResolveResult() is ILVariableResolveResult { Variable: { Kind: VariableKind.Parameter, Index: >= 0 } variable } &&
					variable.Function?.Method is IMethod method && method.Equals(ctor))
					return true;
			}
			return false;
		}

		static Statement CreateAssignment(ConstructorDeclaration ctor, ILVariable thisVariable, IMember member, Expression value) {
			var declaringType = member.DeclaringType;
			var memberResult = new MemberResolveResult(new ThisResolveResult(thisVariable, declaringType), member);
			Expression target;
			// 'this.' if a parameter has the same name
			if (ctor.Parameters.Any(a => a.Name == member.Name)) {
				var thisExpression = new ThisReferenceExpression();
				thisExpression.AddAnnotation(new ThisResolveResult(thisVariable, declaringType));
				target = new MemberReferenceExpression(thisExpression, member.Name);
			}
			else
				target = new IdentifierExpression(member.Name);
			target.AddAnnotation(memberResult);
			var assignment = new AssignmentExpression(target, value);
			assignment.AddAnnotation(new OperatorResolveResult(member.ReturnType, System.Linq.Expressions.ExpressionType.Assign, memberResult, value.GetResolveResult()));
			return new ExpressionStatement(assignment);
		}
	}
}
