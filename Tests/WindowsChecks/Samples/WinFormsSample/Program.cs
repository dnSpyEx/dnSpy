using System;
using System.Linq;
using System.Windows.Forms;

namespace WinFormsSample {
	static class Program {
		[STAThread]
		static void Main(string[] args) {
			ApplicationConfiguration.Initialize();
			using var form = new MainForm();
			if (args.Contains("--selftest")) {
				Console.Out.Write(form.SelfTest());
				Console.Out.Flush();
				return;
			}
			Application.Run(form);
		}
	}
}
