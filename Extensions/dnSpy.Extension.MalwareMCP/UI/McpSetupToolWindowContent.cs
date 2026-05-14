using System;
using System.Windows;
using System.Windows.Input;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;

namespace dnSpy.Extension.MalwareMCP {
	sealed class McpSetupToolWindowContent : ToolWindowContent {
		public static readonly Guid THE_GUID = new("B8FA42A1-1C8F-4C5A-9E86-7F48FEF7B6B1");
		public const AppToolWindowLocation DEFAULT_LOCATION = AppToolWindowLocation.DefaultHorizontal;

		public override Guid Guid => THE_GUID;
		public override string Title => "MCP Server Setup";
		public override object? UIObject => control;
		public override IInputElement? FocusedElement => control?.CommandCopyButton;
		public override FrameworkElement? ZoomElement => control;

		readonly McpSetupControl control;

		public McpSetupToolWindowContent() {
			control = new McpSetupControl();
		}
	}
}
