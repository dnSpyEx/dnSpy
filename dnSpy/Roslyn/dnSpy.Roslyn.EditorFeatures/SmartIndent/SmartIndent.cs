// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using System.Threading;
using dnSpy.Roslyn.EditorFeatures.Extensions;
using dnSpy.Roslyn.Internal.SmartIndent;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Shared.Extensions;
using Microsoft.CodeAnalysis.Text;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Roslyn.Utilities;
using Microsoft.CodeAnalysis.Options;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;

namespace dnSpy.Roslyn.EditorFeatures.SmartIndent {
	class SmartIndent : ISmartIndent {
		readonly ITextView _textView;

		public SmartIndent(ITextView textView) => _textView = textView ?? throw new ArgumentNullException(nameof(textView));

		public int? GetDesiredIndentation(ITextSnapshotLine line) => GetDesiredIndentation(line, CancellationToken.None);

		public void Dispose() { }

		int? GetDesiredIndentation(ITextSnapshotLine line, CancellationToken cancellationToken) {
			if (line == null) {
				throw new ArgumentNullException(nameof(line));
			}

			//using (Logger.LogBlock(FunctionId.SmartIndentation_Start, cancellationToken))
			{
				var document = line.Snapshot.GetOpenDocumentInCurrentContextWithChanges();
				if (document == null)
					return null;

				var newService = document.GetLanguageService<IIndentationService>();
				if (newService == null)
					return null;

				var lineFormattingOptions = new LineFormattingOptions {
					UseTabs = !_textView.Options.IsConvertTabsToSpacesEnabled(),
					IndentationSize = _textView.Options.GetIndentSize(),
					TabSize = _textView.Options.GetTabSize(),
					NewLine = _textView.Options.GetNewLineCharacter()
				};

				var indentationOptions = new IndentationOptions(SyntaxFormattingOptions.CommonDefaults with { LineFormatting = lineFormattingOptions });
				var parsedDocument = ParsedDocument.CreateSynchronously(document, cancellationToken);
				var result = newService.GetIndentation(parsedDocument, line.LineNumber, indentationOptions, cancellationToken);
				return result.GetIndentation(_textView, line);
			}
		}
	}
}
