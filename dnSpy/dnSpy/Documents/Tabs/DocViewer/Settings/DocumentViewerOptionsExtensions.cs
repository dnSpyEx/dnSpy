using dnSpy.Contracts.Text;
using dnSpy.Text.Settings;

namespace dnSpy.Documents.Tabs.DocViewer.Settings {
	static class DocumentViewerOptionsExtensions {
		/// <summary>
		/// Creates an <see cref="Indenter"/> that indents the way the user configured the document viewer
		/// (indent size, tab size, tabs or spaces)
		/// </summary>
		/// <param name="options">Options</param>
		/// <returns></returns>
		public static Indenter CreateIndenter(this ICommonEditorOptions options) =>
			new Indenter(options.IndentSize, options.TabSize, useTabs: !options.ConvertTabsToSpaces);
	}
}
