using System;
using System.Linq;
using System.Windows;

namespace WpfSample {
	public partial class App : Application {
		void App_Startup(object sender, StartupEventArgs e) {
			var window = new MainWindow();
			if (e.Args.Contains("--selftest")) {
				// Runs the window's code without showing it, prints the result and exits
				Console.Out.Write(window.SelfTest());
				Console.Out.Flush();
				Shutdown(0);
				return;
			}
			window.Show();
		}
	}
}
