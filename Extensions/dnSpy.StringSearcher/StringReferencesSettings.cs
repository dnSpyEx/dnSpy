/*
    Copyright (C) 2026 ElektroKill

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
using System.ComponentModel;
using System.ComponentModel.Composition;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Settings;

namespace dnSpy.StringSearcher {
	interface IStringReferencesSettings : INotifyPropertyChanged {
		bool SearchCaseSensitive { get; set; }
		bool SearchIsRegex { get; set; }
		bool SearchMatchFormattedString { get; set; }
	}

	class StringReferencesSettings : ViewModelBase, IStringReferencesSettings {
		public bool SearchCaseSensitive {
			get;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
				}
			}
		}

		public bool SearchIsRegex {
			get;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
				}
			}
		}

		public bool SearchMatchFormattedString {
			get;
			set {
				if (field != value) {
					field = value;
					OnPropertyChanged();
				}
			}
		}

		public StringReferencesSettings Clone() => CopyTo(new StringReferencesSettings());

		public StringReferencesSettings CopyTo(StringReferencesSettings other) {
			other.SearchCaseSensitive = SearchCaseSensitive;
			other.SearchIsRegex = SearchIsRegex;
			other.SearchMatchFormattedString = SearchMatchFormattedString;
			return other;
		}
	}

	[Export, Export(typeof(IStringReferencesSettings))]
	sealed class StringReferencesSettingsImpl : StringReferencesSettings {
		static readonly Guid SETTINGS_GUID = new Guid("9A826601-8DEA-4FC5-8A54-C40A1CF2AFBB");

		readonly ISettingsService settingsService;

		[ImportingConstructor]
		StringReferencesSettingsImpl(ISettingsService settingsService) {
			this.settingsService = settingsService;

			var sect = settingsService.GetOrCreateSection(SETTINGS_GUID);
			SearchCaseSensitive = sect.Attribute<bool?>(nameof(SearchCaseSensitive)) ?? SearchCaseSensitive;
			SearchIsRegex = sect.Attribute<bool?>(nameof(SearchIsRegex)) ?? SearchIsRegex;
			SearchMatchFormattedString = sect.Attribute<bool?>(nameof(SearchMatchFormattedString)) ?? SearchMatchFormattedString;
			PropertyChanged += SearchSettingsImpl_PropertyChanged;
		}

		void SearchSettingsImpl_PropertyChanged(object? sender, PropertyChangedEventArgs e) {
			var sect = settingsService.RecreateSection(SETTINGS_GUID);
			sect.Attribute(nameof(SearchCaseSensitive), SearchCaseSensitive);
			sect.Attribute(nameof(SearchIsRegex), SearchIsRegex);
			sect.Attribute(nameof(SearchMatchFormattedString), SearchMatchFormattedString);
		}
	}
}
