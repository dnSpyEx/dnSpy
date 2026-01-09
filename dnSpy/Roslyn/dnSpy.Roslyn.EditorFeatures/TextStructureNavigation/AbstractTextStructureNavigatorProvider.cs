// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using dnSpy.Roslyn.EditorFeatures.Extensions;
using dnSpy.Roslyn.EditorFeatures.Host;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Operations;
using Microsoft.VisualStudio.Utilities;
using Roslyn.Utilities;

namespace dnSpy.Roslyn.EditorFeatures.TextStructureNavigation {
	abstract partial class AbstractTextStructureNavigatorProvider : ITextStructureNavigatorProvider {
		readonly ITextStructureNavigatorSelectorService _selectorService;
		readonly IContentTypeRegistryService _contentTypeService;
		readonly IWaitIndicator _waitIndicator;

		protected AbstractTextStructureNavigatorProvider(ITextStructureNavigatorSelectorService selectorService,
			IContentTypeRegistryService contentTypeService,
			IWaitIndicator waitIndicator) {
			Contract.ThrowIfNull(selectorService);
			Contract.ThrowIfNull(contentTypeService);

			_selectorService = selectorService;
			_contentTypeService = contentTypeService;
			_waitIndicator = waitIndicator;
		}

		protected abstract bool ShouldSelectEntireTriviaFromStart(SyntaxTrivia trivia);

		protected abstract TextExtent GetExtentOfWordFromToken(ITextStructureNavigator naturalLanguageNavigator, SyntaxToken token, SnapshotPoint position);

		protected static TextExtent GetTokenExtent(SyntaxToken token, ITextSnapshot snapshot)
			=> new(token.Span.ToSnapshotSpan(snapshot), isSignificant: true);

		public ITextStructureNavigator CreateTextStructureNavigator(ITextBuffer subjectBuffer) {
			var naturalLanguageNavigator = _selectorService.CreateTextStructureNavigator(
				subjectBuffer,
				_contentTypeService.GetContentType("any"));

			return new TextStructureNavigator(
				subjectBuffer,
				naturalLanguageNavigator,
				this,
				_waitIndicator);
		}
	}
}
