// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Collections.Generic;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Extensions;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace dnSpy.Roslyn.Internal.QuickInfo.CSharp {
	[ExportQuickInfoProvider(PredefinedQuickInfoProviderNames.Syntactic, LanguageNames.CSharp)]
	internal class SyntacticQuickInfoProvider : AbstractQuickInfoProvider {
		[ImportingConstructor]
		public SyntacticQuickInfoProvider() { }

		protected override Task<QuickInfoContent> BuildContentAsync(QuickInfoContext ctx, SyntaxToken token) {
			if (!token.IsKind(SyntaxKind.CloseBraceToken)) {
				return Task.FromResult<QuickInfoContent>(null);
			}

			// Don't show for interpolations
			if (token.Parent is InterpolationSyntax interpolation &&
				interpolation.CloseBraceToken == token)
			{
				return Task.FromResult<QuickInfoContent>(null);
			}

			// Now check if we can find an open brace.
			var parent = token.Parent!;
			var openBrace = parent.ChildNodesAndTokens().FirstOrDefault(n => n.IsKind(SyntaxKind.OpenBraceToken)).AsToken();
			if (!openBrace.IsKind(SyntaxKind.OpenBraceToken)) {
				return Task.FromResult<QuickInfoContent>(null);
			}

			var spanStart = parent.SpanStart;
			var spanEnd = openBrace.Span.End;

			// If the parent is a scope block, check and include nearby comments around the open brace
			// LeadingTrivia is preferred
			if (IsScopeBlock(parent)) {
				MarkInterestedSpanNearbyScopeBlock(parent, openBrace, ref spanStart, ref spanEnd);
			}
			// If the parent is a child of a property/method declaration, object/array creation, or control flow node..
			// then walk up one higher so we can show more useful context
			else if (parent.GetFirstToken() == openBrace) {
				spanStart = parent.Parent.SpanStart;
			}

			var span = TextSpan.FromBounds(spanStart, spanEnd);
			return Task.FromResult(this.CreateProjectionBufferDeferredContent(span));
		}

		private static bool IsScopeBlock(SyntaxNode node) {
			var parent = node.Parent;
			return node.IsKind(SyntaxKind.Block)
				   && (parent.IsKind(SyntaxKind.Block)
					   || parent.IsKind(SyntaxKind.SwitchSection)
					   || parent.IsKind(SyntaxKind.GlobalStatement));
		}

		private static void MarkInterestedSpanNearbyScopeBlock(SyntaxNode block, SyntaxToken openBrace, ref int spanStart,
			ref int spanEnd) {
			var searchListAbove = openBrace.LeadingTrivia.Reverse();
			if (TryFindFurthestNearbyComment(ref searchListAbove, out var nearbyComment)) {
				spanStart = nearbyComment.SpanStart;
				return;
			}

			var nextToken = block.FindToken(openBrace.FullSpan.End);
			var searchListBelow = nextToken.LeadingTrivia;
			if (TryFindFurthestNearbyComment(ref searchListBelow, out nearbyComment)) {
				spanEnd = nearbyComment.Span.End;
				return;
			}
		}

		private static bool TryFindFurthestNearbyComment<T>(ref T triviaSearchList, out SyntaxTrivia nearbyTrivia)
			where T : IEnumerable<SyntaxTrivia> {
			nearbyTrivia = default(SyntaxTrivia);

			foreach (var trivia in triviaSearchList) {
				if (trivia.IsSingleOrMultiLineComment()) {
					nearbyTrivia = trivia;
				}
				else if (!trivia.IsKind(SyntaxKind.WhitespaceTrivia) && !trivia.IsKind(SyntaxKind.EndOfLineTrivia)) {
					break;
				}
			}

			return nearbyTrivia.IsSingleOrMultiLineComment();
		}
	}
}
