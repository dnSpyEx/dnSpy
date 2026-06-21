using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.AIChat.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dnSpy.AIChat.Services {
	/// <summary>
	/// Azure AI Foundry (Azure OpenAI) provider.
	/// Uses the api-key header and the deployments URL format:
	///   {endpoint}/openai/deployments/{deployment}/chat/completions?api-version=2024-10-21
	/// If the configured endpoint already contains "/chat/completions" it is used as-is.
	/// </summary>
	sealed class AzureFoundryProvider : IChatProvider {
		static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

		public async Task SendAsync(IReadOnlyList<ChatMessage> history, string model, Action<string> onChunk, CancellationToken cancellationToken) {
			var settings = ChatSettings.Load();

			if (string.IsNullOrWhiteSpace(settings.AzureFoundryApiKey))
				throw new InvalidOperationException("No Azure AI Foundry API key configured. Open Settings to add one.");
			if (string.IsNullOrWhiteSpace(settings.AzureFoundryEndpoint))
				throw new InvalidOperationException("No Azure AI Foundry endpoint configured. Open Settings to add one.");
			if (string.IsNullOrWhiteSpace(settings.AzureFoundryDeploymentName))
				throw new InvalidOperationException("No Azure AI Foundry deployment name configured. Open Settings to add one.");

			var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
				? ChatSettings.DefaultSystemPrompt
				: settings.SystemPrompt!.Trim();

			var endpoint = settings.AzureFoundryEndpoint!.Trim().TrimEnd('/');
			var url = endpoint.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase)
				? endpoint
				: $"{endpoint}/openai/deployments/{settings.AzureFoundryDeploymentName}/chat/completions?api-version=2024-10-21";

			var messages = new List<object>();
			messages.Add(new { role = "system", content = systemPrompt });
			foreach (var m in history)
				messages.Add(new { role = m.Role, content = m.Content });

			var body = new {
				messages,
				stream = true,
				max_completion_tokens = 4096,
			};

			using var req = new HttpRequestMessage(HttpMethod.Post, url);
			req.Headers.Add("api-key", settings.AzureFoundryApiKey);
			req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

			using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode) {
				var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				throw new InvalidOperationException($"Azure AI Foundry HTTP {(int)resp.StatusCode}: {err}");
			}

			using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			string? line;
			while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
				cancellationToken.ThrowIfCancellationRequested();
				if (!line.StartsWith("data:", StringComparison.Ordinal))
					continue;
				var payload = line.Substring(5).Trim();
				if (payload.Length == 0 || payload == "[DONE]")
					continue;
				try {
					var obj = JObject.Parse(payload);
					var choices = obj["choices"] as JArray;
					if (choices is null || choices.Count == 0)
						continue;
					var content = (string?)choices[0]?["delta"]?["content"];
					if (!string.IsNullOrEmpty(content))
						onChunk(content!);
				}
				catch (JsonException) { }
			}
		}
	}
}
