/*
    Copyright (C) 2026 dnSpy contributors

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
using System.ComponentModel;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings.Dialog;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.Settings {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class UpstreamSettingsPageProvider : IAppSettingsPageProvider {
		readonly UpstreamLanguageService service;

		[ImportingConstructor]
		UpstreamSettingsPageProvider(UpstreamLanguageService service) => this.service = service;

		public IEnumerable<AppSettingsPage> Create() {
			yield return new UpstreamSettingsPage(service.Settings);
		}
	}

	[ExportAppSettingsModifiedListener(Order = AppSettingsConstants.ORDER_LISTENER_DECOMPILER)]
	sealed class UpstreamSettingsModifiedListener : IAppSettingsModifiedListener {
		readonly UpstreamLanguageService service;
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		UpstreamSettingsModifiedListener(UpstreamLanguageService service, IDocumentTabService documentTabService) {
			this.service = service;
			this.documentTabService = documentTabService;
		}

		public void OnSettingsModified(IAppRefreshSettings appRefreshSettings) {
			// C# and "IL with C#"
			if (appRefreshSettings.Has(UpstreamSettingsPage.RefreshGuid))
				documentTabService.Refresh(service.GetTabs(documentTabService));
		}
	}

	/// <summary>
	/// Options page generated from the settings list, so settings added by new versions of the upstream
	/// decompiler show up without any changes
	/// </summary>
	sealed class UpstreamSettingsPage : AppSettingsPage, IAppSettingsPage2 {
		public static readonly Guid RefreshGuid = new Guid("C0E2B7C1-9E71-4B79-8E77-1B0C5A4D9F21");
		const string OtherGroup = "Other";

		readonly UpstreamDecompilerSettings globalSettings;
		readonly UpstreamDecompilerSettings settings;
		readonly SettingVM[] settingVMs;
		FrameworkElement? uiObject;

		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_DECOMPILER);
		public override Guid Guid => new Guid("6B5A2F1D-3C8E-4F2A-9B7D-2E4C6A8B0D13");
		public override double Order => AppSettingsConstants.ORDER_DECOMPILER_SETTINGS_ILSPY_CSHARP;
		public override string Title => UpstreamCSharpLanguage.LanguageName;
		public override object? UIObject => uiObject ??= CreateUI();

		public UpstreamSettingsPage(UpstreamDecompilerSettings globalSettings) {
			this.globalSettings = globalSettings;
			settings = (UpstreamDecompilerSettings)globalSettings.Clone();
			settingVMs = UpstreamDecompilerSettings.AllSettings.Select(a => new SettingVM(a, settings)).ToArray();
		}

		public override void OnApply() => throw new InvalidOperationException();

		public void OnApply(IAppRefreshSettings appRefreshSettings) {
			if (settings.Equals(globalSettings))
				return;
			settings.CopyTo(globalSettings);
			appRefreshSettings.Add(RefreshGuid);
			// Some settings change which members the decompiler shows inline, and so which ones the tree view hides
			appRefreshSettings.Add(AppSettingsConstants.REFRESH_LANGUAGE_SHOWMEMBER);
		}

		void RefreshCheckBoxes() {
			foreach (var vm in settingVMs)
				vm.Refresh();
		}

		static int GetGroupSortKey(string group) =>
			group == UpstreamDecompilerSettings.DnSpyGroup ? 0 : group == OtherGroup ? 1 : 2;

		FrameworkElement CreateUI() {
			// dnSpy puts the page in a ScrollViewer unless it is one
			var root = new StackPanel { Margin = new Thickness(5) };
			// (A TextControl, not a TextBlock: the Options dialog's search finds and highlights its text)
			root.Children.Add(new TextControl { Content = "Decompiler: " + UpstreamCSharpLanguage.EngineName + " (ICSharpCode.Decompiler)", Margin = new Thickness(0, 0, 0, 5) });

			var presets = Enum.GetValues(typeof(LanguageVersion)).Cast<LanguageVersion>()
				.Where(a => a != LanguageVersion.Preview && a != LanguageVersion.Latest)
				.OrderByDescending(a => (int)a)
				.Select(a => new LanguageVersionVM(a))
				.Prepend(new LanguageVersionVM(LanguageVersion.Latest))
				.ToArray();
			var toolbar = new WrapPanel { Margin = new Thickness(0, 0, 0, 5) };
			toolbar.Children.Add(new Label { Content = "Use the language features of", VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(0, 0, 5, 0) });
			var presetComboBox = new ComboBox { MinWidth = 100, ItemsSource = presets, DisplayMemberPath = nameof(LanguageVersionVM.Name), SelectedIndex = 0, VerticalAlignment = VerticalAlignment.Center };
			toolbar.Children.Add(presetComboBox);
			var applyPresetButton = new Button { Content = "Apply", Margin = new Thickness(5, 0, 0, 0), Padding = new Thickness(10, 1, 10, 1), VerticalAlignment = VerticalAlignment.Center };
			applyPresetButton.Click += (s, e) => {
				if (presetComboBox.SelectedItem is LanguageVersionVM preset) {
					settings.SetLanguageVersion(preset.Version);
					RefreshCheckBoxes();
				}
			};
			toolbar.Children.Add(applyPresetButton);
			var resetButton = new Button { Content = "Restore defaults", Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(10, 1, 10, 1), VerticalAlignment = VerticalAlignment.Center };
			resetButton.Click += (s, e) => {
				foreach (var info in UpstreamDecompilerSettings.AllSettings)
					info.SetValue(settings, info.DefaultValue);
				RefreshCheckBoxes();
			};
			toolbar.Children.Add(resetButton);
			root.Children.Add(toolbar);

			var groups = root;
			var orderedGroups = settingVMs.GroupBy(a => a.Info.Group)
				.OrderBy(g => GetGroupSortKey(g.Key))
				.ThenByDescending(g => g.First().Info.GroupOrder);
			foreach (var group in orderedGroups) {
				var checkBoxes = new StackPanel { Margin = new Thickness(0, 2, 0, 2) };
				foreach (var vm in group) {
					var checkBox = new CheckBox { Content = vm.Description, Margin = new Thickness(0, 2, 0, 2), ToolTip = vm.Info.Name };
					checkBox.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(SettingVM.IsChecked)) { Source = vm, Mode = BindingMode.TwoWay });
					checkBoxes.Children.Add(checkBox);
				}
				groups.Children.Add(new GroupBox { Header = group.Key, Content = checkBoxes, Margin = new Thickness(0, 0, 0, 5) });
			}
			return new ScrollViewer {
				Content = root,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
				HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
			};
		}

		sealed class SettingVM : INotifyPropertyChanged {
			readonly UpstreamDecompilerSettings settings;
			public UpstreamSettingInfo Info { get; }
			public string Description => Info.Description;

			public SettingVM(UpstreamSettingInfo info, UpstreamDecompilerSettings settings) {
				Info = info;
				this.settings = settings;
			}

			public bool IsChecked {
				get => Info.GetValue(settings);
				set {
					if (Info.GetValue(settings) != value) {
						Info.SetValue(settings, value);
						Refresh();
					}
				}
			}

			public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
			public event PropertyChangedEventHandler? PropertyChanged;
		}

		sealed class LanguageVersionVM {
			public LanguageVersion Version { get; }
			public string Name { get; }

			public LanguageVersionVM(LanguageVersion version) {
				Version = version;
				Name = GetName(version);
			}

			// CSharp7_3 -> "C# 7.3", CSharp7 -> "C# 7.0"
			static string GetName(LanguageVersion version) {
				if (version == LanguageVersion.Latest)
					return "Latest C#";
				var name = version.ToString();
				if (!name.StartsWith("CSharp", StringComparison.Ordinal))
					return name;
				name = name.Substring("CSharp".Length).Replace('_', '.');
				if (name.IndexOf('.') < 0)
					name += ".0";
				return "C# " + name;
			}
		}
	}
}
