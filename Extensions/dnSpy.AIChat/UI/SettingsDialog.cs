using System.Windows;
using System.Windows.Controls;
using dnSpy.AIChat.Services;

namespace dnSpy.AIChat.UI {
	static class SettingsDialog {
		public static void Show() {
			var s = ChatSettings.Load();
			var win = new Window {
				Title = "AI Chat Settings",
				Width = 520,
				Height = 600,
				WindowStartupLocation = WindowStartupLocation.CenterOwner,
				Owner = Application.Current?.MainWindow,
				ResizeMode = ResizeMode.CanResize,
			};

			var grid = new Grid { Margin = new Thickness(10) };
			for (int i = 0; i < 10; i++)
				grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
			grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

			TextBox AddRow(int row, string label, string? value) {
				var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center };
				Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0); grid.Children.Add(lbl);
				var tb = new TextBox { Text = value ?? "", Margin = new Thickness(0, 4, 0, 4) };
				Grid.SetRow(tb, row); Grid.SetColumn(tb, 1); grid.Children.Add(tb);
				return tb;
			}

			var anthropic = AddRow(0, "Anthropic API key:", s.AnthropicApiKey);
			var openai = AddRow(1, "OpenAI API key:", s.OpenAIApiKey);
			var openaiUrl = AddRow(2, "OpenAI base URL:", s.OpenAIBaseUrl);
			var cli = AddRow(3, "Claude CLI path:", s.ClaudeCliPath);
			var mcpUrl = AddRow(4, "MCP server URL:", string.IsNullOrWhiteSpace(s.McpServerUrl) ? ChatSettings.DefaultMcpServerUrl : s.McpServerUrl);

			var sysLbl = new TextBlock { Text = "System prompt:", Margin = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Top };
			Grid.SetRow(sysLbl, 5); Grid.SetColumn(sysLbl, 0); grid.Children.Add(sysLbl);
			var sysPrompt = new TextBox {
				Text = string.IsNullOrWhiteSpace(s.SystemPrompt) ? ChatSettings.DefaultSystemPrompt : s.SystemPrompt,
				AcceptsReturn = true,
				TextWrapping = TextWrapping.Wrap,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				Height = 90,
				Margin = new Thickness(0, 4, 0, 4),
				VerticalContentAlignment = VerticalAlignment.Top,
			};
			Grid.SetRow(sysPrompt, 5); Grid.SetColumn(sysPrompt, 1); grid.Children.Add(sysPrompt);

			var foundryEndpoint   = AddRow(6, "Foundry endpoint:",   s.AzureFoundryEndpoint);
			var foundryKey        = AddRow(7, "Foundry API key:",    s.AzureFoundryApiKey);
			var foundryDeployment = AddRow(8, "Foundry deployment:", s.AzureFoundryDeploymentName);

			var info = new TextBlock {
				TextWrapping = TextWrapping.Wrap,
				Opacity = 0.85,
				Margin = new Thickness(0, 12, 0, 0),
				Text = "Tips:\n" +
					"• Claude CLI provider lets you use your Claude Pro/Max subscription. The 'claude' CLI must be installed and logged in (Claude Code).\n" +
					"• Anthropic API requires a separately billed API key from console.anthropic.com.\n" +
					"• OpenAI base URL is optional; leave blank for https://api.openai.com/v1. You can point it at any OpenAI-compatible endpoint (Azure, local LM Studio / Ollama proxy, etc).\n" +
					"• MCP server URL is the dnSpy MCP extension endpoint. The Claude CLI provider injects it via --mcp-config automatically.\n" +				"• Azure AI Foundry: enter the endpoint URL, api-key, and deployment name. Streaming is supported.\n" +					"• Settings are stored at %AppData%\\dnSpy\\AIChat.json.",
			};
			Grid.SetRow(info, 10); Grid.SetColumnSpan(info, 2); Grid.SetColumn(info, 0); grid.Children.Add(info);

			var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
			var ok = new Button { Content = "Save", Padding = new Thickness(14, 3, 14, 3), Margin = new Thickness(0, 0, 6, 0), IsDefault = true };
			var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 3, 14, 3), IsCancel = true };
			buttons.Children.Add(ok); buttons.Children.Add(cancel);
			Grid.SetRow(buttons, 11); Grid.SetColumnSpan(buttons, 2); grid.Children.Add(buttons);

			ok.Click += (_, __) => {
				s.AnthropicApiKey = anthropic.Text;
				s.OpenAIApiKey = openai.Text;
				s.OpenAIBaseUrl = openaiUrl.Text;
				s.ClaudeCliPath = cli.Text;
				s.McpServerUrl = string.IsNullOrWhiteSpace(mcpUrl.Text) ? ChatSettings.DefaultMcpServerUrl : mcpUrl.Text.Trim();
				s.SystemPrompt = string.IsNullOrWhiteSpace(sysPrompt.Text) ? ChatSettings.DefaultSystemPrompt : sysPrompt.Text.Trim();
				s.AzureFoundryEndpoint       = foundryEndpoint.Text.Trim();
				s.AzureFoundryApiKey         = foundryKey.Text.Trim();
				s.AzureFoundryDeploymentName = foundryDeployment.Text.Trim();
				s.Save();
				win.DialogResult = true;
				win.Close();
			};

			win.Content = grid;
			win.ShowDialog();
		}
	}
}
