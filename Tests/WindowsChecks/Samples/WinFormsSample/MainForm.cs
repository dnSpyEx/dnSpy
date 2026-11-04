using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Forms;

namespace WinFormsSample {
	public partial class MainForm : Form {
		readonly List<string> log = new List<string>();

		public MainForm() {
			InitializeComponent();
		}

		void okButton_Click(object? sender, EventArgs e) => log.Add("ok " + nameBox.Text);
		void nameBox_TextChanged(object? sender, EventArgs e) => log.Add("text " + nameBox.Text);
		void itemsList_SelectedIndexChanged(object? sender, EventArgs e) => log.Add("selected " + itemsList.SelectedIndex);

		public string SelfTest() {
			nameBox.Text = "Ada";
			okButton.PerformClick();
			itemsList.SelectedIndex = 1;
			var sb = new StringBuilder();
			sb.AppendLine(string.Join(";", log));
			sb.AppendLine(Text + " " + okButton.Text + " " + itemsList.Items.Count + " " + Properties.Resources.Greeting);
			sb.AppendLine(Controls.Count + " " + statusLabel.Text);
			return sb.ToString();
		}
	}
}
