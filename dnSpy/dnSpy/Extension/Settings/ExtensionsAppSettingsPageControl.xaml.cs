/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows.Controls;
using dnSpy.Contracts.App;
using dnSpy.Contracts.MVVM;

namespace dnSpy.Extension.Settings {
	public sealed class ExtensionInfoVM : ViewModelBase {
		public string Filename { get; }
		public string DisplayName { get; }
		public string Version { get; }
		public string Kind { get; }

		public bool IsEnabled {
			get => isEnabled;
			set {
				if (isEnabled != value) {
					isEnabled = value;
					OnPropertyChanged();
				}
			}
		}
		bool isEnabled;

		public ExtensionInfoVM(string filename, bool isEnabled) {
			Filename = filename;
			DisplayName = Path.GetFileName(filename);
			Version = GetVersion(filename);
			Kind = ExtensionFiles.IsBuiltInExtension(filename) ? "Built-in" : "User";
			this.isEnabled = isEnabled;
		}

		static string GetVersion(string filename) {
			try {
				var name = AssemblyName.GetAssemblyName(filename);
				return name.Version?.ToString() ?? string.Empty;
			}
			catch {
				return string.Empty;
			}
		}
	}

	public partial class ExtensionsAppSettingsPageControl : UserControl {
		public ObservableCollection<ExtensionInfoVM> Items { get; }

		public ExtensionsAppSettingsPageControl() {
			Items = new ObservableCollection<ExtensionInfoVM>();
			InitializeComponent();
			DataContext = this;
			Load();
		}

		void Load() {
			var disabled = ExtensionFiles.LoadDisabled();
			var files = ExtensionFiles.GetExtensionFiles(AppDirectories.BinDirectory)
				.Where(a => File.Exists(a))
				.Select(a => new ExtensionInfoVM(a, !disabled.Contains(Path.GetFileName(a))))
				.OrderBy(a => a.Kind == "Built-in" ? 0 : 1)
				.ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
				.ToArray();

			Items.Clear();
			foreach (var file in files)
				Items.Add(file);

			emptyTextBlock.Visibility = Items.Count == 0 ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
			extensionsListBox.Visibility = Items.Count == 0 ? System.Windows.Visibility.Collapsed : System.Windows.Visibility.Visible;
		}

		public void Save() {
			var disabled = Items.Where(a => !a.IsEnabled).Select(a => a.Filename).ToArray();
			ExtensionFiles.SaveDisabled(disabled);
		}
	}
}
