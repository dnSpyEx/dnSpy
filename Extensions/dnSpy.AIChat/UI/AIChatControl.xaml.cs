using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Input;

namespace dnSpy.AIChat.UI {
	public partial class AIChatControl : UserControl {
		public AIChatControl() {
			InitializeComponent();
			DataContextChanged += (_, __) => HookMessages();
		}

		void HookMessages() {
			if (DataContext is AIChatVM vm) {
				if (vm.Messages is INotifyCollectionChanged ncc) {
					ncc.CollectionChanged -= OnMessagesChanged;
					ncc.CollectionChanged += OnMessagesChanged;
				}
			}
		}

		void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e) {
			Dispatcher.BeginInvoke(new System.Action(() => HistoryScrollViewer.ScrollToBottom()));
		}

		protected override void OnPreviewKeyDown(KeyEventArgs e) {
			if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control) {
				if (DataContext is AIChatVM vm) {
					System.Windows.Input.ICommand cmd = vm.SendCommand;
					if (cmd.CanExecute(null)) {
						cmd.Execute(null);
						e.Handled = true;
						return;
					}
				}
			}
			base.OnPreviewKeyDown(e);
		}
	}
}
