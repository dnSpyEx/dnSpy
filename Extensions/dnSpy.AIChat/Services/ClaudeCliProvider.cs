using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using dnSpy.AIChat.UI;

namespace dnSpy.AIChat.Services {
	/// <summary>
	/// Talks to the locally installed Claude Code CLI ("claude") in non-interactive mode.
	/// Lets the user use their Claude Pro/Max subscription without an API key.
	/// </summary>
	sealed class ClaudeCliProvider : IChatProvider {
		public async Task SendAsync(IReadOnlyList<ChatMessage> history, string model, Action<string> onChunk, CancellationToken cancellationToken) {
			if (history.Count == 0)
				return;

			var settings = ChatSettings.Load();
			var exe = ResolveExe(settings.ClaudeCliPath);
			if (exe is null)
				throw new InvalidOperationException("Could not find the 'claude' CLI on PATH. Install Claude Code or set ClaudeCliPath in AIChat.json.");

			var systemPrompt = string.IsNullOrWhiteSpace(settings.SystemPrompt)
				? ChatSettings.DefaultSystemPrompt
				: settings.SystemPrompt!.Trim();

			var prompt = BuildPrompt(history, systemPrompt);

			// Write MCP config to a temp file so the subprocess always sees the dnSpy MCP
			// server regardless of which working directory the claude process inherits.
			string? mcpTempFile = null;
			var mcpUrl = string.IsNullOrWhiteSpace(settings.McpServerUrl)
				? ChatSettings.DefaultMcpServerUrl
				: settings.McpServerUrl!.Trim();
			if (!string.IsNullOrEmpty(mcpUrl)) {
				await ProbeMcpServerAsync(mcpUrl, cancellationToken).ConfigureAwait(false);
				mcpTempFile = Path.GetTempFileName();
				File.WriteAllText(mcpTempFile, BuildMcpConfig(mcpUrl), Encoding.UTF8);
			}

			var argParts = new List<string> { "-p", QuoteArg(prompt) };
			if (mcpTempFile != null) {
				argParts.Add("--mcp-config");
				argParts.Add(QuoteArg(mcpTempFile));
				// Pre-approve all dnspy MCP tools so Claude doesn't prompt for permission.
				argParts.Add("--allowedTools");
				argParts.Add("mcp__dnspy__*");
			}

			var psi = new ProcessStartInfo {
				FileName = exe,
				Arguments = string.Join(" ", argParts),
				// Run from the user profile directory so that project-scoped MCP configs
				// stored under ~/ in .claude.json are discovered by the subprocess.
				WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
				RedirectStandardInput = true,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding = Encoding.UTF8,
			};

			using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
			try {
				if (!proc.Start())
					throw new InvalidOperationException("Failed to start claude CLI.");

				var stderrTask = Task.Run(() => proc.StandardError.ReadToEndAsync());

				var buffer = new char[1024];
				while (true) {
					cancellationToken.ThrowIfCancellationRequested();
					int read = await proc.StandardOutput.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false);
					if (read <= 0)
						break;
					onChunk(new string(buffer, 0, read));
				}

#if NET5_0_OR_GREATER
				await proc.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
#else
				await Task.Run(() => proc.WaitForExit(), cancellationToken).ConfigureAwait(false);
#endif

				if (proc.ExitCode != 0) {
					var err = await stderrTask.ConfigureAwait(false);
					throw new InvalidOperationException($"claude CLI exited with code {proc.ExitCode}. {err}");
				}
			}
			finally {
				if (mcpTempFile != null)
					try { File.Delete(mcpTempFile); } catch { }
			}
		}

		static readonly HttpClient probeHttp = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };

		static async Task ProbeMcpServerAsync(string mcpUrl, CancellationToken cancellationToken) {
			// Derive the health endpoint from the MCP URL (same host:port, /health path).
			Uri baseUri;
			try { baseUri = new Uri(mcpUrl); } catch { return; } // malformed URL — skip probe
			var healthUrl = new Uri(baseUri, "/health").ToString();
			try {
				using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
				cts.CancelAfter(TimeSpan.FromSeconds(3));
				var resp = await probeHttp.GetAsync(healthUrl, cts.Token).ConfigureAwait(false);
				if (resp.IsSuccessStatusCode)
					return; // server is up
			}
			catch (OperationCanceledException) { throw; }
			catch { }
			throw new InvalidOperationException(
				$"dnSpy MCP server is not reachable at {mcpUrl}.\n" +
				"Make sure the dnSpy MCP extension is loaded and enabled in dnSpy, then try again.");
		}

		// Build a minimal MCP config JSON pointing at the given HTTP server URL.
		static string BuildMcpConfig(string url) {
			url = url.Replace("\\", "\\\\").Replace("\"", "\\\"");
			return $"{{\"mcpServers\":{{\"dnspy\":{{\"type\":\"http\",\"url\":\"{url}\"}}}}}}";
		}

		static string BuildPrompt(IReadOnlyList<ChatMessage> history, string systemPrompt) {
			// Claude CLI -p takes a single prompt; encode the conversation as plain text.
			// Prefix with the system prompt so every invocation carries the context.
			var sb = new StringBuilder();
			if (!string.IsNullOrWhiteSpace(systemPrompt)) {
				sb.AppendLine("<system>");
				sb.AppendLine(systemPrompt);
				sb.AppendLine("</system>");
				sb.AppendLine();
			}
			if (history.Count == 1) {
				sb.Append(history[0].Content);
				return sb.ToString();
			}
			foreach (var m in history) {
				sb.Append(m.Role == "user" ? "User: " : "Assistant: ");
				sb.AppendLine(m.Content);
				sb.AppendLine();
			}
			sb.Append("Assistant:");
			return sb.ToString();
		}

		static string? ResolveExe(string? configured) {
			if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
				return configured;

			foreach (var name in new[] { "claude.cmd", "claude.exe", "claude" }) {
				var found = SearchPath(name);
				if (found != null)
					return found;
			}
			return null;
		}

		static string? SearchPath(string fileName) {
			var pathEnv = Environment.GetEnvironmentVariable("PATH");
			if (string.IsNullOrEmpty(pathEnv))
				return null;
			foreach (var dir in pathEnv.Split(Path.PathSeparator)) {
				try {
					var full = Path.Combine(dir, fileName);
					if (File.Exists(full))
						return full;
				}
				catch { }
			}
			return null;
		}

		// Quote a single Windows process argument per the standard CRT rules.
		static string QuoteArg(string s) {
			if (s.Length > 0 && s.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
				return s;
			var sb = new StringBuilder();
			sb.Append('"');
			int backslashes = 0;
			foreach (var ch in s) {
				if (ch == '\\') { backslashes++; continue; }
				if (ch == '"') {
					sb.Append('\\', backslashes * 2 + 1);
					sb.Append('"');
				}
				else {
					sb.Append('\\', backslashes);
					sb.Append(ch);
				}
				backslashes = 0;
			}
			sb.Append('\\', backslashes * 2);
			sb.Append('"');
			return sb.ToString();
		}
	}
}
