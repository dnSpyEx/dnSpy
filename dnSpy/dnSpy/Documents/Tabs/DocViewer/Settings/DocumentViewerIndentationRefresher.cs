using System.ComponentModel.Composition;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Settings.Groups;
using Microsoft.VisualStudio.Text.Editor;

namespace dnSpy.Documents.Tabs.DocViewer.Settings {
	/// <summary>
	/// Re-decompiles all open tabs when the user changes how the document viewer indents text.
	/// The indentation is baked into the generated text, so the cached decompiled content is
	/// cleared and every tab is refreshed.
	/// </summary>
	[ExportAutoLoaded]
	sealed class DocumentViewerIndentationRefresher : IAutoLoaded {
		readonly IDecompilationCache decompilationCache;
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		DocumentViewerIndentationRefresher(ITextViewOptionsGroupService textViewOptionsGroupService, IDecompilationCache decompilationCache, IDocumentTabService documentTabService) {
			this.decompilationCache = decompilationCache;
			this.documentTabService = documentTabService;
			textViewOptionsGroupService.GetGroup(PredefinedTextViewGroupNames.DocumentViewer).TextViewOptionChanged += TextViewOptionsGroup_TextViewOptionChanged;
		}

		void TextViewOptionsGroup_TextViewOptionChanged(object? sender, TextViewOptionChangedEventArgs e) {
			switch (e.OptionId) {
			case DefaultOptions.ConvertTabsToSpacesOptionName:
			case DefaultOptions.IndentSizeOptionName:
			case DefaultOptions.TabSizeOptionName:
				decompilationCache.ClearAll();
				documentTabService.Refresh(documentTabService.SortedTabs);
				break;
			}
		}
	}
}
