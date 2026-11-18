using System.Runtime.InteropServices;
using System.Windows;
using dnSpy.Text.Editor;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Editor.OptionsExtensionMethods;

namespace dnSpy.Text.Operations {
	static class PlainTextClipboard {
		/// <summary>
		/// Copies the selection, or the current line if nothing is selected, to the clipboard as plain text only.
		/// Unlike <see cref="IEditorOperations.CopySelection"/> no HTML formatting is added.
		/// </summary>
		/// <param name="textView">Text view</param>
		/// <returns></returns>
		public static bool CopySelection(ITextView textView) {
			string text;
			if (textView.Selection.IsEmpty)
				text = textView.Caret.ContainingTextViewLine.ExtentIncludingLineBreak.GetText();
			else
				text = textView.Selection.GetText(textView.Options.GetNewLineCharacter());
			if (text.Length == 0)
				return true;
			try {
				Clipboard.SetText(text);
				return true;
			}
			catch (ExternalException) {
				return false;
			}
		}
	}
}
