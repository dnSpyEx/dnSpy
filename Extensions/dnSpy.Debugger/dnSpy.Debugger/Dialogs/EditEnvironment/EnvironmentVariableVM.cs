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

using System;
using dnSpy.Contracts.Controls.ToolWindows;
using dnSpy.Contracts.MVVM;

namespace dnSpy.Debugger.Dialogs.EditEnvironment {
	sealed class EnvironmentVariableVM : ViewModelBase {
		public string Key {
			get => key;
			set {
				if (key != value) {
					key = value;
					OnPropertyChanged(nameof(Key));
				}
			}
		}
		string key = string.Empty;

		public string Value {
			get => value;
			set {
				if (this.value != value) {
					this.value = value;
					OnPropertyChanged(nameof(Value));
				}
			}
		}
		string value = string.Empty;

		public IEditableValue KeyEditableValue { get; }
		public IEditValueProvider KeyEditValueProvider { get; }
		public IEditableValue ValueEditableValue { get; }
		public IEditValueProvider ValueEditValueProvider { get; }

		public EnvironmentVariableVM(IEditValueProvider keyEditValueProvider, IEditValueProvider valueEditValueProvider) {
			KeyEditValueProvider = keyEditValueProvider;
			KeyEditableValue = new EditableValueImpl(() => Key, s => Key = ConvertEditedString(s));
			ValueEditValueProvider = valueEditValueProvider;
			ValueEditableValue = new EditableValueImpl(() => Value, s => Value = ConvertEditedString(s));
		}

		static string ConvertEditedString(string? s) => string2.IsNullOrWhiteSpace(s) ? string.Empty : s.Trim();
	}
}
