using System.Windows.Controls;

namespace dnSpy.StringSearcher {
	/// <summary>
	/// Interaction logic for StringsControl.xaml
	/// </summary>
	public partial class StringsControl : UserControl {
		public StringsControl() {
			InitializeComponent();
		}

		public ListView ListView => searchListView;
	}
}
