using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.AIChat.UI;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace dnSpy.AIChat.Services {
	sealed class OpenAIApiProvider : IChatProvider {
		static readonly HttpClient http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };

		public async Task SendAsync(IReadOnlyList<ChatMessage> history, string model, Action<string> onChunk, CancellationToken cancellationToken) {
			var settings = ChatSettings.Load();
			if (string.IsNullOrWhiteSpace(settings.OpenAIApiKey))
				throw new InvalidOperationException("No OpenAI API key configured. Open Settings to add one.");

			if (string.IsNullOrWhiteSpace(model))
				model = "gpt-4o-mini";

			var baseUrl = string.IsNullOrWhiteSpace(settings.OpenAIBaseUrl) ? "https://api.openai.com/v1" : settings.OpenAIBaseUrl!.TrimEnd('/');

			var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
				? ChatSettings.DefaultSystemPrompt
				: settings.SystemPrompt!.Trim();

			var messages = new List<object>();
			messages.Add(new { role = "system", content = systemPrompt });
			foreach (var m in history)
				messages.Add(new { role = m.Role, content = m.Content });

			var body = new {
				model,
				stream = true,
				messages,
			};

			using var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/chat/completions");
			req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.OpenAIApiKey);
			req.Content = new StringContent(JsonConvert.SerializeObject(body), Encoding.UTF8, "application/json");

			using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
			if (!resp.IsSuccessStatusCode) {
				var err = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
				throw new InvalidOperationException($"OpenAI API HTTP {(int)resp.StatusCode}: {err}");
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
