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
	sealed class AnthropicApiProvider : IChatProvider {
		static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

		public async Task SendAsync(IReadOnlyList<ChatMessage> history, string model, Action<string> onChunk, CancellationToken cancellationToken) {
			var settings = ChatSettings.Load();
			if (string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
				throw new InvalidOperationException("No Anthropic API key configured. Open Settings to add one.");

			if (string.IsNullOrWhiteSpace(model))
				model = "claude-sonnet-4-5-20250929";

			var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
				? ChatSettings.DefaultSystemPrompt
				: settings.SystemPrompt!.Trim();

			var messages = new List<object>();
			foreach (var m in history)
				messages.Add(new { role = m.Role, content = m.Content });

			var body = new {
				model,
				max_tokens = 4096,
				stream = true,
				system = systemPrompt,
				messages,
			};

			using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
			req.Headers.Add("x-api-key", settings.AnthropicApiKey);
			req.Headers.Add("anthropic-version", "2023-06-01");
			req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

			using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode) {
				var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				throw new InvalidOperationException($"Anthropic API HTTP {(int)resp.StatusCode}: {err}");
			}

			using var stream = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
			using var reader = new StreamReader(stream, Encoding.UTF8);
			string? line;
			while ((line = await reader.ReadLineAsync().ConfigureAwait(false)) != null) {
				cancellationToken.ThrowIfCancellationRequested();
				if (!line.StartsWith("data:", StringComparison.Ordinal))
					continue;
				var payload = line.Substring(5).Trim();
				if (payload.Length == 0)
					continue;
				try {
					var obj = JObject.Parse(payload);
					if ((string?)obj["type"] == "content_block_delta") {
						var text = (string?)obj["delta"]?["text"];
						if (!string.IsNullOrEmpty(text))
							onChunk(text!);
					}
				}
				catch (JsonException) { }
			}
		}
	}
}
