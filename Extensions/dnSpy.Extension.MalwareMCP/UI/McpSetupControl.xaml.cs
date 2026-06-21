using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.Extension.MalwareMCP {
	public partial class McpSetupControl : UserControl {
		public McpSetupControl() {
			InitializeComponent();
			PopulateSetupText();
		}

		public Button CommandCopyButton => CommandCopyButtonField;

		private void PopulateSetupText() {
			var addresses = GetLocalIpv4Addresses().ToList();
			var bestAddress = addresses.FirstOrDefault() ?? "localhost";
			var serverUrl = $"http://{bestAddress}:8765/mcp";
			CommandTextBox.Text = $"claude mcp add dnspy --transport http --url {serverUrl}";
			JsonTextBox.Text = "{\n  \"mcpServers\": {\n    \"dnspy\": {\n      \"type\": \"url\",\n      \"url\": \"" + serverUrl + "\"\n    }\n  }\n}";

			if (addresses.Count == 0) {
				HostInfoTextBlock.Text = "No local IPv4 address could be detected. Replace the host address above with the reachable Windows IP or use localhost if your Claude client runs on the same machine.";
			}
			else if (addresses.Count == 1) {
				HostInfoTextBlock.Text = $"Detected local IPv4 address: {addresses[0]}. Use this address from the Claude host when registering dnSpy.";
			}
			else {
				HostInfoTextBlock.Text = $"Detected local IPv4 addresses: {string.Join(", ", addresses)}. Use the one reachable from Claude.";
			}
		}

		private static IEnumerable<string> GetLocalIpv4Addresses() {
			try {
				return NetworkInterface.GetAllNetworkInterfaces()
					.Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
					.SelectMany(n => n.GetIPProperties().UnicastAddresses)
					.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address))
					.Select(a => a.Address.ToString())
					.Distinct();
			}
			catch {
				return Array.Empty<string>();
			}
		}

		private void CopyCommand_Click(object sender, RoutedEventArgs e) {
			Clipboard.SetText(CommandTextBox.Text);
		}

		private void CopyJson_Click(object sender, RoutedEventArgs e) {
			Clipboard.SetText(JsonTextBox.Text);
		}
	}
}
