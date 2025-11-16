using System.Collections.Generic;
using dnSpy.Contracts.Extension;

namespace dnSpy.StringSearcher {
	[ExportExtension]
	sealed class TheExtension : IExtension {
		public IEnumerable<string> MergedResourceDictionaries {
			get { yield break; }
		}

		public ExtensionInfo ExtensionInfo => new ExtensionInfo {
			ShortDescription = Properties.dnSpy_StringSearcher_Resources.PluginShortDescription,
		};

		public void OnEvent(ExtensionEvent @event, object? obj) {
		}
	}
}
