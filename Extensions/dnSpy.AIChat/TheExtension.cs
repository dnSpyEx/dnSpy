using System.Collections.Generic;
using dnSpy.Contracts.Extension;

namespace dnSpy.AIChat {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = "AI Chat panel for dnSpy (Claude CLI / Anthropic / OpenAI)",
		};

		public void OnEvent(ExtensionEvent @event, object? obj) { }
	}
}
