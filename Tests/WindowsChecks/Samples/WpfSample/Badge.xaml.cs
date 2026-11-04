using System.Windows.Controls;

namespace WpfSample {
	public partial class Badge : UserControl {
		public Badge() => InitializeComponent();
		public string Text => label.Text;
	}
}
