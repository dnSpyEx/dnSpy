using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.AIChat.UI;

namespace dnSpy.AIChat.Services {
	interface IChatProvider {
		Task SendAsync(IReadOnlyList<ChatMessage> history, string model, Action<string> onChunk, CancellationToken cancellationToken);
	}

	static class ChatProviderFactory {
		public static IChatProvider Create(string providerName) => providerName switch {
			"Anthropic API" => new AnthropicApiProvider(),
			"OpenAI API" => new OpenAIApiProvider(),
			"Azure AI Foundry" => new AzureFoundryProvider(),
			_ => new ClaudeCliProvider(),
		};
	}
}
