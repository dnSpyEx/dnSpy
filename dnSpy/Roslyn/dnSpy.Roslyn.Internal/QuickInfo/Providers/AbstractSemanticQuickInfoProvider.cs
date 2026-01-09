// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.DocumentationComments;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.LanguageService;
using Microsoft.CodeAnalysis.QuickInfo;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using Roslyn.Utilities;

namespace dnSpy.Roslyn.Internal.QuickInfo {
	internal abstract partial class AbstractSemanticQuickInfoProvider : AbstractQuickInfoProvider {
		public AbstractSemanticQuickInfoProvider() { }

		protected override async Task<QuickInfoContent> BuildContentAsync(QuickInfoContext context, SyntaxToken token) {
			var (tokenInformation, supportedPlatforms) = await ComputeQuickInfoDataAsync(context, token).ConfigureAwait(false);
			if (tokenInformation.Symbols.IsDefaultOrEmpty)
				return null;

			var cancellationToken = context.CancellationToken;
			var semanticModel = await context.Document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
			var services = context.Document.Project.Solution.Workspace.Services;

			return await CreateContentAsync(services, semanticModel, token, tokenInformation,
				supportedPlatforms, context.Options, cancellationToken).ConfigureAwait(false);
		}

		private async Task<(TokenInformation tokenInformation, SupportedPlatformData supportedPlatforms)>
			ComputeQuickInfoDataAsync(QuickInfoContext context, SyntaxToken token)
		{
			var cancellationToken = context.CancellationToken;
			var document = context.Document;

			var linkedDocumentIds = document.GetLinkedDocumentIds();
			if (linkedDocumentIds.Any())
				return await ComputeFromLinkedDocumentsAsync(context, token, linkedDocumentIds).ConfigureAwait(false);

			var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
			var services = document.Project.Solution.Services;
			var tokenInformation = BindToken(services, semanticModel, token, cancellationToken);
			return (tokenInformation, supportedPlatforms: null);
		}

		private async Task<(TokenInformation, SupportedPlatformData supportedPlatforms)> ComputeFromLinkedDocumentsAsync(
			QuickInfoContext context,
			SyntaxToken token,
			ImmutableArray<DocumentId> linkedDocumentIds) {
			// Linked files/shared projects: imagine the following when GOO is false
			// #if GOO
			// int x = 3;
			// #endif
			// var y = x$$;
			//
			// 'x' will bind as an error type, so we'll show incorrect information.
			// Instead, we need to find the head in which we get the best binding,
			// which in this case is the one with no errors.

			var cancellationToken = context.CancellationToken;
			var document = context.Document;
			var solution = document.Project.Solution;
			var services = solution.Services;

			var semanticModel = await document.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
			var mainTokenInformation = BindToken(services, semanticModel, token, cancellationToken);

			var candidateResults = new List<(DocumentId docId, TokenInformation tokenInformation)>
			{
				(document.Id, mainTokenInformation)
			};

			foreach (var linkedDocumentId in linkedDocumentIds)
			{
				var linkedDocument = solution.GetRequiredDocument(linkedDocumentId);
				var linkedModel = await linkedDocument.GetRequiredSemanticModelAsync(cancellationToken).ConfigureAwait(false);
				var linkedToken = FindTokenInLinkedDocument(token, linkedModel, cancellationToken);

				if (linkedToken != default)
				{
					// Not in an inactive region, so this file is a candidate.
					var linkedSymbols = BindToken(services, linkedModel, linkedToken, cancellationToken);
					candidateResults.Add((linkedDocumentId, linkedSymbols));
				}
			}

			// Take the first result with no errors.
			// If every file binds with errors, take the first candidate, which is from the current file.
			var bestBinding = candidateResults.FirstOrNull(c => HasNoErrors(c.tokenInformation.Symbols))
							  ?? candidateResults.First();

			if (bestBinding.tokenInformation.Symbols.IsDefaultOrEmpty)
				return default;

			// We calculate the set of projects that are candidates for the best binding
			var candidateProjects = candidateResults.SelectAsArray(result => result.docId.ProjectId);

			// We calculate the set of supported projects
			var invalidProjects = ImmutableArray.CreateBuilder<ProjectId>();
			candidateResults.Remove(bestBinding);
			foreach (var (docId, tokenInformation) in candidateResults)
			{
				// Does the candidate have anything remotely equivalent?
				if (!tokenInformation.Symbols.Intersect(bestBinding.tokenInformation.Symbols, LinkedFilesSymbolEquivalenceComparer.Instance).Any())
					invalidProjects.Add(docId.ProjectId);
			}

			var supportedPlatforms = new SupportedPlatformData(solution, invalidProjects.ToImmutable(), candidateProjects);
			return (bestBinding.tokenInformation, supportedPlatforms);
		}

		private static bool HasNoErrors(ImmutableArray<ISymbol> symbols) => symbols.Length > 0
																			&&
																			!ErrorVisitor.ContainsError(symbols.FirstOrDefault());

		private static SyntaxToken FindTokenInLinkedDocument(SyntaxToken token,
			SemanticModel linkedModel,
			CancellationToken cancellationToken) {
			var root = linkedModel.SyntaxTree.GetRoot(cancellationToken);
			if (root == null)
				return default;

			// Don't search trivia because we want to ignore inactive regions
			var linkedToken = root.FindToken(token.SpanStart);

			// The new and old tokens should have the same span?
			return token.Span == linkedToken.Span ? linkedToken : default;
		}

		protected async Task<QuickInfoContent> CreateContentAsync(HostWorkspaceServices services,
			SemanticModel semanticModel,
			SyntaxToken token,
			TokenInformation tokenInformation,
			SupportedPlatformData supportedPlatforms,
			SymbolDescriptionOptions options,
			CancellationToken cancellationToken) {
			var syntaxFactsService =
				services.GetLanguageServices(semanticModel.Language).GetRequiredService<ISyntaxFactsService>();

			var symbols = tokenInformation.Symbols;

			// if generating quick info for an attribute, prefer bind to the class instead of the constructor
			if (syntaxFactsService.IsNameOfAttribute(token.Parent!)) {
				symbols = [.. symbols.OrderBy((s1, s2) =>
					s1.Kind == s2.Kind ? 0 :
					s1.Kind == SymbolKind.NamedType ? -1 :
					s2.Kind == SymbolKind.NamedType ? 1 : 0)];
			}

			return await CreateContentAsync(services, semanticModel, token, symbols,
				supportedPlatforms, tokenInformation.ShowAwaitReturn, tokenInformation.NullableFlowState, options,
				cancellationToken).ConfigureAwait(false);
		}

		protected abstract bool GetBindableNodeForTokenIndicatingLambda(SyntaxToken token, out SyntaxNode found);
		protected abstract bool GetBindableNodeForTokenIndicatingPossibleIndexerAccess(SyntaxToken token, out SyntaxNode found);
		protected abstract bool GetBindableNodeForTokenIndicatingMemberAccess(SyntaxToken token, out SyntaxToken found);

		protected virtual NullableFlowState GetNullabilityAnalysis(SemanticModel semanticModel, ISymbol symbol, SyntaxNode node,
			CancellationToken cancellationToken) => NullableFlowState.None;

		private TokenInformation BindToken(SolutionServices services, SemanticModel semanticModel, SyntaxToken token,
			CancellationToken cancellationToken) {
			var languageServices = services.GetLanguageServices(semanticModel.Language);
			var syntaxFacts = languageServices.GetRequiredService<ISyntaxFactsService>();
			var enclosingType = semanticModel.GetEnclosingNamedType(token.SpanStart, cancellationToken);

			var bindableParent = syntaxFacts.TryGetBindableParent(token);

			var symbolSet = new HashSet<ISymbol>(SymbolEquivalenceComparer.Instance);
			var filteredSymbols = ImmutableArray.CreateBuilder<ISymbol>();

			AddSymbols(GetSymbolsFromToken(token, services, semanticModel, cancellationToken), checkAccessibility: true);
			AddSymbols(bindableParent != null ? semanticModel.GetMemberGroup(bindableParent, cancellationToken) : [], checkAccessibility: false);

			if (filteredSymbols is [var firstSymbol, ..])
			{
				var isAwait = syntaxFacts.IsAwaitKeyword(token);
				var nullableFlowState = NullableFlowState.None;
				if (bindableParent != null)
				{
					nullableFlowState = GetNullabilityAnalysis(semanticModel, firstSymbol, bindableParent, cancellationToken);
				}

				return new TokenInformation(filteredSymbols.ToImmutable(), isAwait, nullableFlowState);
			}

			// Couldn't bind the token to specific symbols.  If it's an operator, see if we can at
			// least bind it to a type.
			if (syntaxFacts.IsOperator(token))
			{
				var typeInfo = semanticModel.GetTypeInfo(token.Parent!, cancellationToken);
				if (IsOk(typeInfo.Type))
					return new TokenInformation([typeInfo.Type]);
			}

			return default;

			void AddSymbols(ImmutableArray<ISymbol> symbols, bool checkAccessibility)
			{
				foreach (var symbol in symbols)
				{
					if (!IsOk(symbol))
						continue;

					if (checkAccessibility && !IsAccessible(symbol, enclosingType))
						continue;

					if (symbolSet.Add(symbol))
						filteredSymbols.Add(symbol);
				}
			}
		}

		private ImmutableArray<ISymbol> GetSymbolsFromToken(SyntaxToken token, SolutionServices services,
			SemanticModel semanticModel, CancellationToken cancellationToken) {
			if (GetBindableNodeForTokenIndicatingLambda(token, out var lambdaSyntax)) {
				var symbol = semanticModel.GetSymbolInfo(lambdaSyntax, cancellationToken).Symbol;
				return symbol != null ? [symbol] : [];
			}

			if (GetBindableNodeForTokenIndicatingPossibleIndexerAccess(token, out var elementAccessExpression)) {
				var symbol = semanticModel.GetSymbolInfo(elementAccessExpression, cancellationToken).Symbol;
				if (symbol?.IsIndexer() == true) {
					return [symbol];
				}
			}

			if (GetBindableNodeForTokenIndicatingMemberAccess(token, out var accessedMember)) {
				// If the cursor is on the dot in an invocation `x.M()`, then we'll consider the cursor was placed on `M`
				token = accessedMember;
			}

			return semanticModel.GetSemanticInfo(token, services, cancellationToken)
								.GetSymbols(includeType: true);
		}

		private static bool IsOk(ISymbol symbol) {
			if (symbol == null)
				return false;

			if (symbol.IsErrorType())
				return false;

			if (symbol is ITypeParameterSymbol { TypeParameterKind: TypeParameterKind.Cref })
				return false;

			return true;
		}

		private static bool IsAccessible(ISymbol symbol, INamedTypeSymbol within) => within == null
																					 || symbol.IsAccessibleWithin(within);

		protected async Task<QuickInfoContent> CreateContentAsync(HostWorkspaceServices services,
			SemanticModel semanticModel,
			SyntaxToken token,
			ImmutableArray<ISymbol> symbols,
			SupportedPlatformData supportedPlatforms,
			bool showAwaitReturn,
			NullableFlowState flowState,
			SymbolDescriptionOptions options,
			CancellationToken cancellationToken) {
			var descriptionService = services.GetLanguageServices(semanticModel.Language).GetService<ISymbolDisplayService>();

			var sections = await descriptionService.ToDescriptionGroupsAsync(semanticModel, token.SpanStart,
				symbols.AsImmutable(), options, cancellationToken).ConfigureAwait(false);

			var symbol = symbols.First();

			var mainDescriptionBuilder = new List<TaggedText>();
			if (sections.TryGetValue(SymbolDescriptionGroups.MainDescription, out var parts)) {
				mainDescriptionBuilder.AddRange(parts);
			}

			var typeParameterMapBuilder = new List<TaggedText>();
			if (sections.TryGetValue(SymbolDescriptionGroups.TypeParameterMap, out parts)) {
				if (!parts.IsDefaultOrEmpty) {
					typeParameterMapBuilder.AddLineBreak();
					typeParameterMapBuilder.AddRange(parts);
				}
			}

			var anonymousTypesBuilder = new List<TaggedText>();
			if (sections.TryGetValue(SymbolDescriptionGroups.StructuralTypes, out parts)) {
				if (!parts.IsDefaultOrEmpty) {
					anonymousTypesBuilder.AddLineBreak();
					anonymousTypesBuilder.AddRange(parts);
				}
			}

			var usageTextBuilder = new List<TaggedText>();
			if (sections.TryGetValue(SymbolDescriptionGroups.AwaitableUsageText, out parts)) {
				if (!parts.IsDefaultOrEmpty) {
					usageTextBuilder.AddRange(parts);
				}
			}

			if (supportedPlatforms != null) {
				usageTextBuilder.AddRange(supportedPlatforms.ToDisplayParts().ToTaggedText());
			}

			var exceptionsTextBuilder = new List<TaggedText>();
			if (sections.TryGetValue(SymbolDescriptionGroups.Exceptions, out parts)) {
				if (!parts.IsDefaultOrEmpty) {
					exceptionsTextBuilder.AddRange(parts);
				}
			}

			var formatter = services.GetLanguageServices(semanticModel.Language)
									.GetService<IDocumentationCommentFormattingService>();
			var syntaxFactsService = services.GetLanguageServices(semanticModel.Language).GetService<ISyntaxFactsService>();
			var documentationContent = GetDocumentationContent(symbols, sections, semanticModel, token, formatter,
				syntaxFactsService, cancellationToken);
			var showWarningGlyph = supportedPlatforms != null && supportedPlatforms.HasValidAndInvalidProjects();
			var showSymbolGlyph = true;

			if (showAwaitReturn && (symbol as INamedTypeSymbol)?.SpecialType == SpecialType.System_Void) {
				documentationContent = CreateDocumentationCommentDeferredContent(null);
				showSymbolGlyph = false;
			}

			return this.CreateQuickInfoDisplayDeferredContent(
				symbol: symbol,
				showWarningGlyph: showWarningGlyph,
				showSymbolGlyph: showSymbolGlyph,
				mainDescription: mainDescriptionBuilder,
				documentation: documentationContent,
				typeParameterMap: typeParameterMapBuilder,
				anonymousTypes: anonymousTypesBuilder,
				usageText: usageTextBuilder,
				exceptionText: exceptionsTextBuilder);
		}

		private ImmutableArray<TaggedText> GetDocumentationContent(IEnumerable<ISymbol> symbols,
			IDictionary<SymbolDescriptionGroups, ImmutableArray<TaggedText>> sections,
			SemanticModel semanticModel,
			SyntaxToken token,
			IDocumentationCommentFormattingService formatter,
			ISyntaxFactsService syntaxFactsService,
			CancellationToken cancellationToken) {
			if (sections.TryGetValue(SymbolDescriptionGroups.Documentation, out var parts)) {
				var documentationBuilder = new List<TaggedText>();
				documentationBuilder.AddRange(parts);
				return CreateClassifiableDeferredContent(documentationBuilder);
			}
			else if (symbols.Any()) {
				var symbol = symbols.First().OriginalDefinition;

				// if generating quick info for an attribute, bind to the class instead of the constructor
				if (syntaxFactsService.IsNameOfAttribute(token.Parent) &&
					symbol.ContainingType?.IsAttribute() == true) {
					symbol = symbol.ContainingType;
				}

				var documentation = symbol.GetDocumentationParts(semanticModel, token.SpanStart, formatter, cancellationToken);

				if (documentation != null) {
					return CreateClassifiableDeferredContent(documentation.ToList());
				}
			}

			return CreateDocumentationCommentDeferredContent(null);
		}
	}
}
