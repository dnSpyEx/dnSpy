using System;
using System.ComponentModel.Composition;
using System.Windows.Input;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dnSpy.Extension.MalwareMCP {
	[ExportAutoLoaded]
	sealed class McpSetupToolWindowLoader : IAutoLoaded {
		public static readonly RoutedCommand OpenMcpSetup = new("OpenMcpSetup", typeof(McpSetupToolWindowLoader));

		[ImportingConstructor]
		McpSetupToolWindowLoader(IWpfCommandService wpfCommandService, IDsToolWindowService toolWindowService) {
			var cmds = wpfCommandService.GetCommands(ControlConstants.GUID_MAINWINDOW);
			cmds.Add(OpenMcpSetup, new RelayCommand(_ => toolWindowService.Show(McpSetupToolWindowContent.THE_GUID)));
		}
	}

	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_HELP_GUID, Header = "MCP Server Setup", Group = MenuConstants.GROUP_APP_MENU_HELP_LINKS, Order = 1000)]
	sealed class OpenMcpSetupMenuItem : MenuItemCommand {
		OpenMcpSetupMenuItem() : base(McpSetupToolWindowLoader.OpenMcpSetup) { }
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class McpSetupToolWindowContentProvider : IToolWindowContentProvider {
		readonly Lazy<McpSetupToolWindowContent> content;

		[ImportingConstructor]
		McpSetupToolWindowContentProvider() => content = new Lazy<McpSetupToolWindowContent>(() => new McpSetupToolWindowContent());

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(McpSetupToolWindowContent.THE_GUID, McpSetupToolWindowContent.DEFAULT_LOCATION, 0, false); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) => guid == McpSetupToolWindowContent.THE_GUID ? content.Value : null;
	}
}
