using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Windows;
using System.Windows.Input;
using dnSpy.AIChat.UI;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dnSpy.AIChat {
	[ExportAutoLoaded]
	sealed class AIChatToolWindowLoader : IAutoLoaded {
		public static readonly RoutedCommand OpenAIChat = new RoutedCommand("OpenAIChat", typeof(AIChatToolWindowLoader));

		[ImportingConstructor]
		AIChatToolWindowLoader(IWpfCommandService wpfCommandService, IDsToolWindowService toolWindowService) {
			var cmds = wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			cmds.Add(OpenAIChat, new RelayCommand(_ => toolWindowService.Show(AIChatToolWindowContent.THE_GUID)));
			cmds.Add(OpenAIChat, ModifierKeys.Control | ModifierKeys.Alt, Key.I);
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_VIEW_GUID, Header = "AI _Chat", InputGestureText = "Ctrl+Alt+I", Group = MenuConstants.GROUP_APP_MENU_VIEW_WINDOWS, Order = 3000)]
	sealed class OpenAIChatMenuItem : MenuItemCommand {
		OpenAIChatMenuItem() : base(AIChatToolWindowLoader.OpenAIChat) { }
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class AIChatToolWindowContentProvider : IToolWindowContentProvider {
		readonly Lazy<AIChatToolWindowContent> content;

		[ImportingConstructor]
		AIChatToolWindowContentProvider() => content = new Lazy<AIChatToolWindowContent>(() => new AIChatToolWindowContent());

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(AIChatToolWindowContent.THE_GUID, AIChatToolWindowContent.DEFAULT_LOCATION, 0, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) =>
			guid == AIChatToolWindowContent.THE_GUID ? content.Value : null;
	}

	sealed class AIChatToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new Guid("4F8C3F8E-7D7A-4D8B-9F6F-1D4F2E3A5C90");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => "AI Chat";
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control.PromptTextBox;
		public override FrameworkElement? ZoomElement => control;

		readonly AIChatControl control;
		readonly AIChatVM vm;

		public AIChatToolWindowContent() {
			vm = new AIChatVM();
			control = new AIChatControl { DataContext = vm };
		}

		public override void OnVisibilityChanged(ToolWindowContentVisibilityEvent visEvent) {
			if (visEvent == ToolWindowContentVisibilityEvent.Removed)
				vm.Dispose();
		}
	}
}
