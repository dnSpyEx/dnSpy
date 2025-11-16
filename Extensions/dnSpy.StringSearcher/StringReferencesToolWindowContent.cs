using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dnSpy.StringSearcher {
	[Export(typeof(IToolWindowContentProvider))]
	sealed class StringReferencesToolWindowContentProvider : IToolWindowContentProvider {
		private readonly Lazy<IStringReferencesService> service;

		public StringReferencesToolWindowContent DocumentTreeViewWindowContent
			=> toolWindowContent ??= new StringReferencesToolWindowContent(service);
		StringReferencesToolWindowContent? toolWindowContent;

		[ImportingConstructor]
		StringReferencesToolWindowContentProvider(Lazy<IStringReferencesService> stringSearcher) {
			this.service = stringSearcher;
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get {
				yield return new ToolWindowContentInfo(
					StringReferencesToolWindowContent.THE_GUID,
					StringReferencesToolWindowContent.DEFAULT_LOCATION,
					AppToolWindowConstants.DEFAULT_CONTENT_ORDER_BOTTOM_ANALYZER,
					false
				);
			}
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == StringReferencesToolWindowContent.THE_GUID ? DocumentTreeViewWindowContent : null;
	}

	sealed class StringReferencesToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new("EF36BC9C-4F48-45AC-8A0B-BC2C11A3194E");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override object? UIObject => service.Value.UIObject;

		public override IInputElement? FocusedElement => service.Value.FocusedElement;

		public override FrameworkElement? ZoomElement => service.Value.ZoomElement;

		public override Guid Guid => THE_GUID;

		public override string Title => Properties.dnSpy_StringSearcher_Resources.StringReferencesWindowTitle;

		readonly Lazy<IStringReferencesService> service;

		public StringReferencesToolWindowContent(Lazy<IStringReferencesService> service) {
			this.service = service;
		}
	}
}
