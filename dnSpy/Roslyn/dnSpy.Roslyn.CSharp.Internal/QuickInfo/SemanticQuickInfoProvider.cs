// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Composition;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.ErrorReporting;
using Microsoft.CodeAnalysis.GoToDefinition;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Internal.Log;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;
using NullableFlowState = Microsoft.CodeAnalysis.NullableFlowState;
using NullableAnnotation = Microsoft.CodeAnalysis.NullableAnnotation;

namespace dnSpy.Roslyn.Internal.QuickInfo.CSharp {
	[ExportQuickInfoProvider(PredefinedQuickInfoProviderNames.Semantic, LanguageNames.CSharp)]
	internal class SemanticQuickInfoProvider : AbstractSemanticQuickInfoProvider {
		[ImportingConstructor]
		public SemanticQuickInfoProvider() { }

		/// <summary>
		/// If the token is the '=>' in a lambda, or the 'delegate' in an anonymous function,
		/// return the syntax for the lambda or anonymous function.
		/// </summary>
		protected override bool GetBindableNodeForTokenIndicatingLambda(SyntaxToken token, out SyntaxNode found) {
			if (token.IsKind(SyntaxKind.EqualsGreaterThanToken)
				&& token.Parent is (kind: SyntaxKind.ParenthesizedLambdaExpression or SyntaxKind.SimpleLambdaExpression)) {
				// () =>
				found = token.Parent;
				return true;
			}
			else if (token.IsKind(SyntaxKind.DelegateKeyword) && token.Parent.IsKind(SyntaxKind.AnonymousMethodExpression)) {
				// delegate (...) { ... }
				found = token.Parent;
				return true;
			}

			found = null;
			return false;
		}

		protected override bool GetBindableNodeForTokenIndicatingPossibleIndexerAccess(SyntaxToken token, out SyntaxNode found) {
			if (token.Kind() is SyntaxKind.CloseBracketToken or SyntaxKind.OpenBracketToken &&
				token.Parent?.Parent.IsKind(SyntaxKind.ElementAccessExpression) == true) {
				found = token.Parent.Parent;
				return true;
			}

			found = null;
			return false;
		}

		protected override bool GetBindableNodeForTokenIndicatingMemberAccess(SyntaxToken token, out SyntaxToken found) {
			if (token.IsKind(SyntaxKind.DotToken) &&
				token.Parent is MemberAccessExpressionSyntax memberAccess) {
				found = memberAccess.Name.Identifier;
				return true;
			}

			found = default;
			return false;
		}

		protected override bool ShouldCheckPreviousToken(SyntaxToken token) => !token.Parent.IsKind(SyntaxKind.XmlCrefAttribute);

		protected override NullableFlowState GetNullabilityAnalysis(SemanticModel semanticModel, ISymbol symbol, SyntaxNode node,
			CancellationToken cancellationToken) {
			// Anything less than C# 8 we just won't show anything, even if the compiler could theoretically give analysis
			if (semanticModel.SyntaxTree.Options.LanguageVersion() < LanguageVersion.CSharp8) {
				return NullableFlowState.None;
			}

			// If the user doesn't have nullable enabled, don't show anything. For now we're not trying to be more precise if the user has just annotations or just
			// warnings. If the user has annotations off then things that are oblivious might become non-null (which is a lie) and if the user has warnings off then
			// that probably implies they're not actually trying to know if their code is correct. We can revisit this if we have specific user scenarios.
			var nullableContext = semanticModel.GetNullableContext(node.SpanStart);
			if (!nullableContext.WarningsEnabled() || !nullableContext.AnnotationsEnabled()) {
				return NullableFlowState.None;
			}

			// When hovering over the 'var' keyword, give the nullability of the variable being declared there. e.g.
			//
			//  $$var v = ...;
			//
			// Should say both the type of 'v' and say if 'v' is non-null/null at this point.
			if (node is IdentifierNameSyntax { IsVar: true, Parent: VariableDeclarationSyntax syntax } && syntax.Variables is { Count: 1 } &&
				syntax.Variables[0] is { } declarator)
			{
				// Recurse back into GetNullabilityAnalysis which acts as if the user asked for QI on the
				// variable declarator itself.
				var variable = semanticModel.GetDeclaredSymbol(declarator, cancellationToken);
				if (variable is ILocalSymbol local)
					return GetNullabilityAnalysis(semanticModel, local, declarator, cancellationToken);
			}

			// Although GetTypeInfo can return nullability for uses of all sorts of things, it's not always useful for quick info.
			// For example, if you have a call to a method with a nullable return, the fact it can be null is already captured
			// in the return type shown -- there's no flow analysis information there.
			switch (symbol) {
			// Ignore constant values for nullability flow state
			case IFieldSymbol { HasConstantValue: true }: return default;
			case ILocalSymbol { HasConstantValue: true }: return default;

			// Symbols with useful quick info
			case IFieldSymbol _:
			case ILocalSymbol _:
			case IParameterSymbol _:
			case IPropertySymbol _:
			case IRangeVariableSymbol _:
				break;

			// Although methods have no nullable flow state,
			// we still want to show when they are "not nullable aware".
			case IMethodSymbol { ReturnsVoid: false }:
				break;

			default:
				return default;
			}

			if (symbol.GetMemberType() is { IsValueType: false, NullableAnnotation: NullableAnnotation.None })
				return NullableFlowState.NotNull;

			var typeInfo = GetTypeInfo(semanticModel, symbol, node, cancellationToken);

			// Nullability is a reference type only feature, value types can use
			// something like "int?"  to be nullable but that ends up encasing as
			// Nullable<int>, which isn't exactly the same. To avoid confusion and
			// extra noise, we won't show nullable flow state for value types
			if (typeInfo.Type?.IsValueType == true) {
				return default;
			}

			return typeInfo.Nullability.FlowState;

			static TypeInfo GetTypeInfo(SemanticModel semanticModel, ISymbol symbol, SyntaxNode node, CancellationToken cancellationToken)
			{
				// We may be on the declarator of some local like:
				//
				// string x = "";
				// var $$y = 1;
				//
				// In this case, 'y' will have the type 'string?'.  But we'll still want to say that it is has a non-null
				// value to begin with.

				if (symbol is ILocalSymbol && node is VariableDeclaratorSyntax
					{
						Parent: VariableDeclarationSyntax { Type.IsVar: true },
						Initializer.Value: { } initializer,
					})
				{
					return semanticModel.GetTypeInfo(initializer, cancellationToken);
				}
				else
				{
					return semanticModel.GetTypeInfo(node, cancellationToken);
				}
			}
		}
	}
}
