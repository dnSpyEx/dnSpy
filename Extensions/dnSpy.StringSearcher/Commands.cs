/*
    Copyright (C) 2025 Washi

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using dnlib.DotNet;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Contracts.TreeView;

namespace dnSpy.StringSearcher {
	[ExportAutoLoaded]
	sealed class StringSearcherCommandLoader : IAutoLoaded {
		[ImportingConstructor]
		StringSearcherCommandLoader(IWpfCommandService wpfCommandService, Lazy<IStringReferencesService> analyzerService) {
			var cmds = wpfCommandService.GetCommands(new Guid(StringSearcherConstants.GUID_STRINGS_LISTBOX));
			cmds.Add(ApplicationCommands.Copy,
				(s, e) => CopyStringLiteralCommand.ExecuteInternal(analyzerService.Value.CurrentReference),
				(s, e) => e.CanExecute = analyzerService.Value.CurrentReference is not null
			);
		}
	}

	abstract class FindStringReferencesInModuleCommand(IDsToolWindowService toolWindowService, IStringReferencesService stringReferencesService) : MenuItemBase {

		public override bool IsVisible(IMenuItemContext context) => GetModules(context).Any();

		public override void Execute(IMenuItemContext context) {
			var modules = GetModules(context).ToArray();
			if (modules.Length == 0)
				return;

			toolWindowService.Show(StringReferencesToolWindowContent.THE_GUID);
			stringReferencesService.Analyze(modules);
		}

		protected abstract IEnumerable<ModuleDef> GetModules(IMenuItemContext context);

		[ExportMenuItem(Header = "res:FindStringReferencesInModuleCommand", Icon = DsImagesAttribute.Search, Group = MenuConstants.GROUP_CTX_DOCUMENTS_OTHER, Order = 0)]
		sealed class DocumentsCommand : FindStringReferencesInModuleCommand {

			[ImportingConstructor]
			public DocumentsCommand(IDsToolWindowService toolWindowService, IStringReferencesService stringReferencesService) 
				: base(toolWindowService, stringReferencesService) {
			}

			protected override IEnumerable<ModuleDef> GetModules(IMenuItemContext context) {
				var nodes = context.Find<TreeNodeData[]>();
				if (nodes is null)
					return [];

				return nodes.OfType<DocumentTreeNodeData>()
					.SelectMany(n => n.GetModule()?.Assembly.Modules ?? [])
					.Distinct();
			}
		}

		[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:FindStringReferencesInModuleCommand", Icon = DsImagesAttribute.Search, Group = MenuConstants.GROUP_APP_MENU_EDIT_FIND, Order = 20)]
		sealed class MenuBarCommand : FindStringReferencesInModuleCommand {
			private readonly IDocumentTabService documentTabService;

			[ImportingConstructor]
			public MenuBarCommand(IDsToolWindowService toolWindowService, IStringReferencesService stringReferencesService, IDocumentTabService documentTabService)
				: base(toolWindowService, stringReferencesService) {
				this.documentTabService = documentTabService;
			}

			protected override IEnumerable<ModuleDef> GetModules(IMenuItemContext context) {
				return documentTabService.DocumentTreeView.TreeView.SelectedItems
					.OfType<DocumentTreeNodeData>()
					.SelectMany(n => n.GetModule()?.Assembly.Modules ?? [])
					.Distinct();
			}
		}
	}

	abstract class ReferenceCommandBase(Lazy<IStringReferencesService> service) : MenuItemBase {
		public Lazy<IStringReferencesService> Service { get; } = service;

		public override void Execute(IMenuItemContext context) {
			var reference = GetReference(context);
			if (reference is null)
				return;
			Execute(context, reference);
		}

		protected abstract void Execute(IMenuItemContext context, StringReference reference);

		public override bool IsVisible(IMenuItemContext context) => GetReference(context) is not null;

		private static StringReference? GetReference(IMenuItemContext context) {
			if (context.CreatorObject.Guid != new Guid(StringSearcherConstants.GUID_STRINGS_LISTBOX))
				return null;

			return context.Find<StringReference>();
		}
	}

	[ExportMenuItem(Header = "res:CopyStringLiteralCommand", Group = StringSearcherConstants.GUID_CTX_GROUP_COPY, Order = 0, Icon = DsImagesAttribute.Copy, InputGestureText = "res:ShortCutKeyCtrlC")]
	sealed class CopyStringLiteralCommand : ReferenceCommandBase {
		[ImportingConstructor]
		CopyStringLiteralCommand(Lazy<IStringReferencesService> service)
			: base(service) {
		}

		protected override void Execute(IMenuItemContext context, StringReference reference) => ExecuteInternal(reference);

		internal static void ExecuteInternal(StringReference? reference) {
			if (reference is null) {
				return;
			}

			try {
				Clipboard.SetText(reference.FormattedLiteral);
			}
			catch (ExternalException) { }
		}
	}

	[ExportMenuItem(Header = "res:CopyRawStringLiteralCommand", Group = StringSearcherConstants.GUID_CTX_GROUP_COPY, Order = 1)]
	sealed class CopyRawStringLiteralCommand : ReferenceCommandBase {
		[ImportingConstructor]
		CopyRawStringLiteralCommand(Lazy<IStringReferencesService> service)
			: base(service) {
		}

		protected override void Execute(IMenuItemContext context, StringReference reference) => ExecuteInternal(reference);

		internal static void ExecuteInternal(StringReference? reference) {
			if (reference is null) {
				return;
			}

			try {
				Clipboard.SetText(reference.Literal, TextDataFormat.UnicodeText);
			}
			catch (ExternalException) { }
		}
	}

	abstract class OpenReferenceCommandBase : ReferenceCommandBase {
		readonly bool newTab;

		protected OpenReferenceCommandBase(Lazy<IStringReferencesService> service, bool newTab) 
			: base(service) {
			this.newTab = newTab;
		}

		protected override void Execute(IMenuItemContext context, StringReference reference) => Service.Value.FollowReference(reference, newTab);
	}

	[ExportMenuItem(Header = "res:GoToReferenceInCodeCommand", InputGestureText = "res:DoubleClick", Group = StringSearcherConstants.GUID_CTX_GROUP_FOLLOW, Order = 0)]
	sealed class OpenReferenceCommand : OpenReferenceCommandBase {
		[ImportingConstructor]
		OpenReferenceCommand(Lazy<IStringReferencesService> service)
			: base(service, false) {
		}
	}

	[ExportMenuItem(Header = "res:GoToReferenceInCodeNewTabCommand", Group = StringSearcherConstants.GUID_CTX_GROUP_FOLLOW, Order = 1)]
	sealed class OpenReferenceNewTabCommand : OpenReferenceCommandBase {
		[ImportingConstructor]
		OpenReferenceNewTabCommand(Lazy<IStringReferencesService> service)
			: base(service, true) {
		}
	}
}
