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

using dnSpy.Contracts.Controls;

namespace dnSpy.Debugger.Dialogs.EditEnvironment {
	public partial class EditEnvironmentDlg : WindowBase {
		public EditEnvironmentDlg() {
			InitializeComponent();
			listView.SelectionChanged += (_, e) => {
				if (e.AddedItems.Count > 0)
					listView.ScrollIntoView(e.AddedItems[e.AddedItems.Count - 1]);
				else if (listView.SelectedItems.Count > 0)
					listView.ScrollIntoView(listView.SelectedItems[listView.SelectedItems.Count - 1]);
			};
		}
	}
}
