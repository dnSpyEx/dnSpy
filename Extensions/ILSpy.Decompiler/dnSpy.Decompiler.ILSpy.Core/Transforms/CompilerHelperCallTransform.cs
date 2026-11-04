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

using System.Linq;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Transforms {
	/// <summary>
	/// Replaces calls of the throw helpers the C# compiler adds to the assembly ('&lt;PrivateImplementationDetails&gt;.
	/// ThrowSwitchExpressionException(value);') with the throw statements they contain, so the code compiles (Edit
	/// Method etc.). Upstream turns them back into switch expressions when it recognizes the switch; when it doesn't, it
	/// shows the call. A no-op if there are no such calls. It runs in all views: the throw is what the source code had.
	/// </summary>
	sealed class CompilerHelperCallTransform : IAstTransform {
		const string SwitchExpressionException = "System.Runtime.CompilerServices.SwitchExpressionException";
		const string InvalidOperationException = "System.InvalidOperationException";

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var statement in rootNode.Descendants.OfType<ExpressionStatement>().ToArray()) {
				if (statement.Expression is not InvocationExpression invocation || invocation.GetSymbol() is not IMethod method || !method.IsStatic)
					continue;
				var declType = method.DeclaringTypeDefinition;
				if (declType is null || declType.DeclaringTypeDefinition is not null || declType.Name != "<PrivateImplementationDetails>" || declType.Namespace.Length != 0)
					continue;
				string? exceptionTypeName = method.Name switch {
					"ThrowSwitchExpressionException" when method.Parameters.Count == 1 => SwitchExpressionException,
					"ThrowSwitchExpressionExceptionNoValue" when method.Parameters.Count == 0 => SwitchExpressionException,
					"ThrowInvalidOperationException" when method.Parameters.Count == 0 => InvalidOperationException,
					_ => null,
				};
				if (exceptionTypeName is null)
					continue;
				var exceptionType = context.TypeSystem.FindType(new FullTypeName(exceptionTypeName));
				if (exceptionType.Kind == TypeKind.Unknown)
					continue;
				var creation = new ObjectCreateExpression(context.TypeSystemAstBuilder.ConvertType(exceptionType));
				if (method.Parameters.Count == 1) {
					var argument = invocation.Arguments.Single();
					argument.Remove();
					// The helper calls SwitchExpressionException(object unmatchedValue): without a cast, a string or an
					// exception would be the message or the inner exception
					if (!argument.GetResolveResult().Type.IsKnownType(KnownTypeCode.Object))
						argument = new CastExpression(context.TypeSystemAstBuilder.ConvertType(context.TypeSystem.FindType(KnownTypeCode.Object)), argument);
					creation.Arguments.Add(argument);
				}
				var throwStatement = new ThrowStatement(creation);
				throwStatement.CopyAnnotationsFrom(statement);
				statement.ReplaceWith(throwStatement);
			}
		}
	}
}
