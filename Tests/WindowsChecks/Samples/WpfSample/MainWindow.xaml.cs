using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WpfSample {
	public partial class MainWindow : Window {
		readonly List<string> log = [];

		public MainWindow() => InitializeComponent();

		void Window_Loaded(object sender, RoutedEventArgs e) => log.Add("loaded");
		void OkButton_Click(object sender, RoutedEventArgs e) => log.Add("ok " + nameBox.Text);
		void Toolbar_Click(object sender, RoutedEventArgs e) => log.Add("toolbar " + ((FrameworkElement)e.OriginalSource).Name);
		void NameBox_TextChanged(object sender, TextChangedEventArgs e) => log.Add("text " + nameBox.Text);
		void Items_SelectionChanged(object sender, SelectionChangedEventArgs e) => log.Add("selected " + items.SelectedIndex);
		void Item_MouseDoubleClick(object sender, MouseButtonEventArgs e) => log.Add("double click");

		public string SelfTest() {
			nameBox.Text = "Ada";
			okButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, okButton));
			cancelButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent, cancelButton));
			items.SelectedIndex = 1;
			var item = (ListBoxItem)items.Items[0];
			item.Style = (Style)FindResource("ItemStyle");
			item.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left) { RoutedEvent = Control.MouseDoubleClickEvent });
			var sb = new StringBuilder();
			sb.AppendLine(string.Join(";", log));
			sb.AppendLine(toolbar.Children.Count + " " + badge.Text + " " + Title);
			return sb.ToString();
		}
	}
}
