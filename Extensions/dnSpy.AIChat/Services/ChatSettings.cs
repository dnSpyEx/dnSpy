using System;
using System.IO;
using Newtonsoft.Json;

namespace dnSpy.AIChat.Services {
	sealed class ChatSettings {
		public string? Provider { get; set; }
		public string? Model { get; set; }
		public string? AnthropicApiKey { get; set; }
		public string? OpenAIApiKey { get; set; }
		public string? OpenAIBaseUrl { get; set; }
		public string? ClaudeCliPath { get; set; }
		/// <summary>MCP server URL injected via --mcp-config when using the Claude CLI provider.</summary>
		public string? McpServerUrl { get; set; }
		/// <summary>Persistent system prompt sent on every request so the AI always knows its context.</summary>
		public string? SystemPrompt { get; set; }

		// Azure AI Foundry
		public string? AzureFoundryEndpoint { get; set; }
		public string? AzureFoundryApiKey { get; set; }
		public string? AzureFoundryDeploymentName { get; set; }

		public const string DefaultMcpServerUrl = "http://localhost:8765/mcp";

		public const string DefaultSystemPrompt =
			"You are an AI assistant integrated into dnSpy, a .NET assembly browser and debugger. " +
			"The dnSpy MCP server is ALREADY running and the mcp__dnspy__* tools are ALREADY available in this session — " +
			"do NOT tell the user to install, register, or configure the MCP server; it is done automatically. " +
			"Always use the mcp__dnspy__* tools to inspect assemblies, types, methods, and IL bytecode loaded in dnSpy " +
			"rather than guessing or asking the user to paste code. " +
			"When asked to analyse, decompile, or explain code, call the relevant mcp__dnspy__ tools first.";

		static string SettingsPath {
			get {
				var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "dnSpy");
				Directory.CreateDirectory(dir);
				return Path.Combine(dir, "AIChat.json");
			}
		}

		public static ChatSettings Load() {
			try {
				if (File.Exists(SettingsPath)) {
					var json = File.ReadAllText(SettingsPath);
					return JsonConvert.DeserializeObject<ChatSettings>(json) ?? new ChatSettings();
				}
			}
			catch { }
			return new ChatSettings();
		}

		public void Save() {
			try {
				var json = JsonConvert.SerializeObject(this, Formatting.Indented);
				File.WriteAllText(SettingsPath, json);
			}
			catch { }
		}

		public static string DefaultMcpConfigFor(string? url) =>
			string.IsNullOrWhiteSpace(url) ? DefaultMcpServerUrl : url!;

		public static string DefaultModelFor(string provider) => provider switch {
			"Anthropic API" => "claude-sonnet-4-5-20250929",
			"OpenAI API" => "gpt-4o-mini",
			_ => "",
		};
	}
}
