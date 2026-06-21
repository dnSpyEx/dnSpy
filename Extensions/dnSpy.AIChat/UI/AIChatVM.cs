using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using dnSpy.AIChat.Services;
using dnSpy.Contracts.MVVM;
using Newtonsoft.Json;

namespace dnSpy.AIChat.UI {
	sealed class AIChatVM : ViewModelBase, IDisposable {
		public ObservableCollection<ChatMessage> Messages { get; } = new();
		public string[] Providers { get; } = new[] { "Claude CLI (subscription)", "Anthropic API", "OpenAI API", "Azure AI Foundry" };

		string selectedProvider = "Claude CLI (subscription)";
		public string SelectedProvider {
			get => selectedProvider;
			set {
				if (selectedProvider != value) {
					selectedProvider = value;
					OnPropertyChanged(nameof(SelectedProvider));
					var settings = ChatSettings.Load();
					settings.Provider = value;
					settings.Save();
					Model = ChatSettings.DefaultModelFor(value);
				}
			}
		}

		string model = "";
		public string Model {
			get => model;
			set {
				if (model != value) {
					model = value ?? "";
					OnPropertyChanged(nameof(Model));
					var settings = ChatSettings.Load();
					settings.Model = model;
					settings.Save();
				}
			}
		}

		string promptText = "";
		public string PromptText {
			get => promptText;
			set {
				if (promptText != value) {
					promptText = value ?? "";
					OnPropertyChanged(nameof(PromptText));
					CommandManager.InvalidateRequerySuggested();
				}
			}
		}

		string status = "Ready";
		public string Status {
			get => status;
			set { if (status != value) { status = value; OnPropertyChanged(nameof(Status)); } }
		}

		bool isSending;
		public bool IsSending {
			get => isSending;
			private set {
				if (isSending != value) {
					isSending = value;
					OnPropertyChanged(nameof(IsSending));
					CommandManager.InvalidateRequerySuggested();
				}
			}
		}

		bool includeSelection;
		public bool IncludeSelection {
			get => includeSelection;
			set { if (includeSelection != value) { includeSelection = value; OnPropertyChanged(nameof(IncludeSelection)); } }
		}

		public RelayCommand SendCommand { get; }
		public RelayCommand CancelCommand { get; }
		public RelayCommand ClearCommand { get; }
		public RelayCommand OpenSettingsCommand { get; }

		CancellationTokenSource? cts;

		static string HistoryFilePath => Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
			"dnSpy", "AIChat-history.json");

		public AIChatVM() {
			var s = ChatSettings.Load();
			var provider = string.IsNullOrEmpty(s.Provider) ? Providers[0] : s.Provider!;
			selectedProvider = provider;
			model = string.IsNullOrEmpty(s.Model) ? ChatSettings.DefaultModelFor(provider) : s.Model!;

			SendCommand = new RelayCommand(_ => _ = SendAsync(), _ => !IsSending && !string.IsNullOrWhiteSpace(PromptText));
			CancelCommand = new RelayCommand(_ => cts?.Cancel(), _ => IsSending);
			ClearCommand = new RelayCommand(_ => { Messages.Clear(); SaveHistory(); });
			OpenSettingsCommand = new RelayCommand(_ => SettingsDialog.Show());

			LoadHistory();
		}

		void LoadHistory() {
			try {
				var path = HistoryFilePath;
				if (!File.Exists(path)) return;
				var entries = JsonConvert.DeserializeObject<HistoryEntry[]>(File.ReadAllText(path));
				if (entries == null) return;
				foreach (var e in entries)
					if (!string.IsNullOrEmpty(e.Role))
						Messages.Add(new ChatMessage(e.Role!, e.Content ?? ""));
			}
			catch { }
		}

		void SaveHistory() {
			try {
				var path = HistoryFilePath;
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				File.WriteAllText(path, JsonConvert.SerializeObject(
					Messages.Select(m => new HistoryEntry { Role = m.Role, Content = m.Content }).ToList()));
			}
			catch { }
		}

		async Task SendAsync() {
			var prompt = PromptText.Trim();
			if (prompt.Length == 0)
				return;

			if (IncludeSelection) {
				var sel = SelectionContextProvider.TryGetSelection();
				if (!string.IsNullOrEmpty(sel))
					prompt = prompt + "\n\n--- dnSpy current selection ---\n" + sel;
			}

			Messages.Add(new ChatMessage("user", prompt));
			PromptText = "";
			IsSending = true;
			Status = "Sending…";

			var assistant = new ChatMessage("assistant", "");
			Messages.Add(assistant);

			cts = new CancellationTokenSource();
			try {
				IChatProvider provider = ChatProviderFactory.Create(SelectedProvider);
				var history = new System.Collections.Generic.List<ChatMessage>(Messages);
				history.RemoveAt(history.Count - 1); // remove the placeholder

				await provider.SendAsync(history, Model, chunk => {
					Application.Current?.Dispatcher.Invoke(() => assistant.Append(chunk));
				}, cts.Token).ConfigureAwait(false);

				Application.Current?.Dispatcher.Invoke(() => {
					Status = "Ready";
					SaveHistory();
				});
			}
			catch (OperationCanceledException) {
				Application.Current?.Dispatcher.Invoke(() => {
					assistant.Append("\n[cancelled]");
					Status = "Cancelled";
					SaveHistory();
				});
			}
			catch (Exception ex) {
				Application.Current?.Dispatcher.Invoke(() => {
					assistant.Append("\n[error] " + ex.Message);
					Status = "Error";
					SaveHistory();
				});
			}
			finally {
				cts?.Dispose();
				cts = null;
				Application.Current?.Dispatcher.Invoke(() => IsSending = false);
			}
		}

		public void Dispose() {
			cts?.Cancel();
			cts?.Dispose();
		}

		sealed class HistoryEntry {
			public string? Role { get; set; }
			public string? Content { get; set; }
		}
	}
}
