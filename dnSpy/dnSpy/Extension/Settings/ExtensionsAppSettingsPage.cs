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
using System.ComponentModel.Composition;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Extension.Settings {
	[Export(typeof(IAppSettingsPageProvider))]
	sealed class ExtensionsAppSettingsPageProvider : IAppSettingsPageProvider {
		public IEnumerable<AppSettingsPage> Create() {
			yield return new ExtensionsAppSettingsPage();
		}
	}

	sealed class ExtensionsAppSettingsPage : AppSettingsPage {
		internal static readonly Guid PageGuid = new Guid("6D0C4E2B-8A31-4C9E-9F5D-2B7A1C3E8D64");

		public override Guid Guid => PageGuid;
		public override Guid ParentGuid => new Guid(AppSettingsConstants.GUID_ENVIRONMENT);
		public override double Order => 2500;
		public override string Title => "Extensions";
		public override object? UIObject => control;

		readonly ExtensionsAppSettingsPageControl control;

		public ExtensionsAppSettingsPage() => control = new ExtensionsAppSettingsPageControl();

		public override void OnApply() => control.Save();
	}
}
