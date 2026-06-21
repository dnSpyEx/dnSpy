using System.ComponentModel.Composition;
using System.Text;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Extension;

namespace dnSpy.AIChat.Services {
	[ExportAutoLoaded]
	sealed class SelectionContextProvider : IAutoLoaded {
		readonly IDocumentTabService documentTabService;
		static SelectionContextProvider? instance;

		[ImportingConstructor]
		SelectionContextProvider(IDocumentTabService documentTabService) {
			this.documentTabService = documentTabService;
			instance = this;
		}

		public static string? TryGetSelection() {
			var inst = instance;
			if (inst is null)
				return null;
			var tab = inst.documentTabService.ActiveTab;
			if (tab?.UIContext is IDocumentViewer dv) {
				var selection = dv.TextView.Selection;
				if (!selection.IsEmpty) {
					var sb = new StringBuilder();
					foreach (var span in selection.SelectedSpans)
						sb.Append(span.GetText());
					if (sb.Length > 0)
						return sb.ToString();
				}
				return dv.Content.Text;
			}
			return null;
		}
	}
}
