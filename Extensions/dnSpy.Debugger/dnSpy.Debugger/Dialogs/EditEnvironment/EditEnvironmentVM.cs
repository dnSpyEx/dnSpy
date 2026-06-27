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
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows.Input;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Controls.ToolWindows;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Text;
using dnSpy.Debugger.Properties;

namespace dnSpy.Debugger.Dialogs.EditEnvironment {
	sealed class EditEnvironmentVM : ViewModelBase {
		readonly EditValueProviderService editValueProviderService;
		readonly DbgEnvironment originalEnvironment;

		public ObservableCollection<EnvironmentVariableVM> EnvironmentVariables { get; }
		public ObservableCollection<EnvironmentVariableVM> SelectedItems { get; }

		IEditValueProvider KeyEditValueProvider => keyEditValueProvider ?? (keyEditValueProvider = editValueProviderService.Create(ContentTypes.EnvironmentVariableKey, Array.Empty<string>()));
		IEditValueProvider? keyEditValueProvider;
		IEditValueProvider ValueEditValueProvider => valueEditValueProvider ?? (valueEditValueProvider = editValueProviderService.Create(ContentTypes.EnvironmentVariableValue, Array.Empty<string>()));
		IEditValueProvider? valueEditValueProvider;

		public ICommand AddEnvironmentVariableCommand => new RelayCommand(a => AddEnvironmentVariable());
		public ICommand AddManyEnvironmentVariablesCommand => new RelayCommand(a => AddManyEnvironmentVariables());
		public ICommand RemoveEnvironmentVariableCommand => new RelayCommand(a => RemoveEnvironmentVariable(), a => SelectedItems.Count > 0);
		public ICommand ResetCommand => new RelayCommand(a => Reset());

		public EditEnvironmentVM(EditValueProviderService editValueProviderService) : this(editValueProviderService, new DbgEnvironment()) { }

		public EditEnvironmentVM(EditValueProviderService editValueProviderService, DbgEnvironment environment) {
			this.editValueProviderService = editValueProviderService;
			originalEnvironment = environment;

			EnvironmentVariables = new ObservableCollection<EnvironmentVariableVM>();
			SelectedItems = new ObservableCollection<EnvironmentVariableVM>();

			InitializeFrom(environment);
		}

		void AddEnvironmentVariable() {
			SelectedItems.Clear();
			var vm = new EnvironmentVariableVM(KeyEditValueProvider, ValueEditValueProvider);
			EnvironmentVariables.Add(vm);
			SelectedItems.Add(vm);
		}

		void AddManyEnvironmentVariables() {
			var variables = MsgBox.Instance.Ask(dnSpy_Debugger_Resources.AddEnvironmentVariablesMsgBoxLabel, null, dnSpy_Debugger_Resources.AddEnvironmentVariablesMsgBoxTitle, s => {
				new EnvironmentStringParser(s).TryParse(out var env);
				return env;
			}, s => new EnvironmentStringParser(s).TryParse(out _) ? null : dnSpy_Debugger_Resources.InvalidInputString);
			if (variables is null)
				return;

			SelectedItems.Clear();
			foreach (var pair in variables) {
				var variableVm = new EnvironmentVariableVM(KeyEditValueProvider, ValueEditValueProvider) {
					Key = pair.Key,
					Value = pair.Value
				};
				EnvironmentVariables.Add(variableVm);
				SelectedItems.Add(variableVm);
			}
		}

		void RemoveEnvironmentVariable() {
			var oldSelectedIndex = EnvironmentVariables.IndexOf(SelectedItems[0]);

			var itemsToRemove = new List<EnvironmentVariableVM>(SelectedItems);
			SelectedItems.Clear();
			foreach (var environmentVariableVm in itemsToRemove)
				EnvironmentVariables.Remove(environmentVariableVm);

			SelectedItems.Add(EnvironmentVariables[Math.Min(oldSelectedIndex, EnvironmentVariables.Count - 1)]);
		}

		void Reset() {
			SelectedItems.Clear();
			EnvironmentVariables.Clear();
			InitializeFrom(originalEnvironment);
		}

		void InitializeFrom(DbgEnvironment environment) {
			foreach (var pair in environment.Environment) {
				EnvironmentVariables.Add(new EnvironmentVariableVM(KeyEditValueProvider, ValueEditValueProvider) {
					Key = pair.Key,
					Value = pair.Value
				});
			}
		}

		public void CopyTo(DbgEnvironment environment) {
			environment.Clear();
			foreach (var vm in EnvironmentVariables) {
				if (string.IsNullOrEmpty(vm.Key))
					continue;
				environment.Add(vm.Key, vm.Value);
			}
		}
	}
}
