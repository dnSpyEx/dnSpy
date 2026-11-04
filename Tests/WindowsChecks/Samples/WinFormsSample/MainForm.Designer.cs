#nullable enable
namespace WinFormsSample {
	partial class MainForm {
		private System.ComponentModel.IContainer? components = null;

		protected override void Dispose(bool disposing) {
			if (disposing && (components != null))
				components.Dispose();
			base.Dispose(disposing);
		}

		#region Windows Form Designer generated code
		private void InitializeComponent() {
			okButton = new System.Windows.Forms.Button();
			nameBox = new System.Windows.Forms.TextBox();
			itemsList = new System.Windows.Forms.ListBox();
			statusStrip = new System.Windows.Forms.StatusStrip();
			statusLabel = new System.Windows.Forms.ToolStripStatusLabel();
			statusStrip.SuspendLayout();
			SuspendLayout();
			okButton.Location = new System.Drawing.Point(12, 12);
			okButton.Name = "okButton";
			okButton.Size = new System.Drawing.Size(75, 23);
			okButton.TabIndex = 0;
			okButton.Text = "OK";
			okButton.UseVisualStyleBackColor = true;
			okButton.Click += okButton_Click;
			nameBox.Location = new System.Drawing.Point(93, 12);
			nameBox.Name = "nameBox";
			nameBox.Size = new System.Drawing.Size(150, 23);
			nameBox.TabIndex = 1;
			nameBox.TextChanged += nameBox_TextChanged;
			itemsList.FormattingEnabled = true;
			itemsList.Items.AddRange(new object[] { "First", "Second", "Third" });
			itemsList.Location = new System.Drawing.Point(12, 41);
			itemsList.Name = "itemsList";
			itemsList.Size = new System.Drawing.Size(231, 94);
			itemsList.TabIndex = 2;
			itemsList.SelectedIndexChanged += itemsList_SelectedIndexChanged;
			statusStrip.Items.AddRange(new System.Windows.Forms.ToolStripItem[] { statusLabel });
			statusStrip.Location = new System.Drawing.Point(0, 150);
			statusStrip.Name = "statusStrip";
			statusStrip.Size = new System.Drawing.Size(260, 22);
			statusLabel.Name = "statusLabel";
			statusLabel.Text = "Ready";
			AutoScaleDimensions = new System.Drawing.SizeF(7F, 15F);
			AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
			ClientSize = new System.Drawing.Size(260, 172);
			Controls.Add(statusStrip);
			Controls.Add(itemsList);
			Controls.Add(nameBox);
			Controls.Add(okButton);
			Name = "MainForm";
			Text = "WinForms sample";
			statusStrip.ResumeLayout(false);
			statusStrip.PerformLayout();
			ResumeLayout(false);
			PerformLayout();
		}
		#endregion

		private System.Windows.Forms.Button okButton;
		private System.Windows.Forms.TextBox nameBox;
		private System.Windows.Forms.ListBox itemsList;
		private System.Windows.Forms.StatusStrip statusStrip;
		private System.Windows.Forms.ToolStripStatusLabel statusLabel;
	}
}
