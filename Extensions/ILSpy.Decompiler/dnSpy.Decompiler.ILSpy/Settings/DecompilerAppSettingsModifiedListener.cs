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

using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Settings.Dialog;

namespace dnSpy.Decompiler.ILSpy.Settings {
	[ExportAppSettingsModifiedListener(Order = AppSettingsConstants.ORDER_LISTENER_DECOMPILER)]
	sealed class DecompilerAppSettingsModifiedListener : IAppSettingsModifiedListener {
		readonly IDocumentTabService documentTabService;

		[ImportingConstructor]
		DecompilerAppSettingsModifiedListener(IDocumentTabService documentTabService) => this.documentTabService = documentTabService;

		public void OnSettingsModified(IAppRefreshSettings appRefreshSettings) {
			// The C# settings page refreshes "IL with C#" too (UpstreamSettingsModifiedListener)
			if (appRefreshSettings.Has(SettingsConstants.REDISASSEMBLE_IL_ILSPY_CODE)) {
				RefreshCode<Core.IL.ILDecompiler>();
				RefreshCode<Core.Mixed.ILWithCSharpDecompiler>();
			}
		}

		IEnumerable<(IDocumentTab tab, IDecompiler decompiler)> DecompilerTabs {
			get {
				foreach (var tab in documentTabService.VisibleFirstTabs) {
					var decompiler = (tab.Content as IDecompilerTabContent)?.Decompiler;
					if (decompiler is not null)
						yield return (tab, decompiler);
				}
			}
		}

		void RefreshCode<T>() => documentTabService.Refresh(DecompilerTabs.Where(t => t.decompiler is T).Select(a => a.tab).ToArray());
	}
}
