/*
	Copyright (C) 2024 ElektroKill

	This file is part of dnSpy

	dnSpy is free software: you can redistribute it and/or modify
	it under the terms of the GNU General Public License as published by
	the Free Software Foundation, either version 3 of the License, or
	(at your option) any later version.

	dnSpy is distributed in the hope that it will be useful,
	but WITHOUT ANY WARRANTY; without even the implied warranty of
	MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
	GNU General Public License for more details.

	You should have received a copy of the GNU General Public License
	along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.Reflection;
using System.Windows.Input;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Menus;

namespace dnSpy.MainApp {
	[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_FILE_GUID, Header = "res:RestartAsAdministratorCommand", Group = MenuConstants.GROUP_APP_MENU_FILE_EXIT, Order = 900000)]
	sealed class RestartAsAdministratorCommand : MenuItemBase {
		readonly IAppWindow appWindow;

		[ImportingConstructor]
		public RestartAsAdministratorCommand(IAppWindow appWindow) => this.appWindow = appWindow;

		public override bool IsVisible(IMenuItemContext context) => !Constants.IsRunningAsAdministrator;

		public override void Execute(IMenuItemContext context) {
			appWindow.MainWindowClosing += OnMainWindowClosing;
			((ICommand)ApplicationCommands.Close).Execute(context);
		}

		void OnMainWindowClosing(object? sender, CancelEventArgs args) {
			appWindow.MainWindowClosing -= OnMainWindowClosing;
			// If a different handler canceled the close operation, don't restart.
			if (args.Cancel)
				return;
			try {
				Process.Start(new ProcessStartInfo(Constants.ExecutablePath) { UseShellExecute = true, Verb = "runas" });
			}
			catch {
				args.Cancel = true;
			}
		}
	}
}
