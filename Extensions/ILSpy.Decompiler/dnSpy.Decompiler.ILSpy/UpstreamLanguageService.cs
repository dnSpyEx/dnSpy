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
using System.ComponentModel.Composition;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Settings;
using dnSpy.Decompiler.ILSpy.Core;
#if DEBUG
using dnSpy.Decompiler.ILSpy.Core.ILAst;
#endif
using dnSpy.Decompiler.ILSpy.Core.Mixed;
using dnSpy.Decompiler.ILSpy.Core.Settings;

namespace dnSpy.Decompiler.ILSpy {
	/// <summary>
	/// Owns the decompiler instance and its settings, which are saved in dnSpy's settings file
	/// </summary>
	[Export]
	sealed class UpstreamLanguageService {
		static readonly Guid SETTINGS_GUID = new Guid("2E5E8E5F-5B1B-4C61-9C2C-7A2B8E3C4D11");

		readonly ISettingsService settingsService;
		bool disableSave;

		public UpstreamDecompilerSettings Settings { get; }
		public UpstreamCSharpLanguage Language { get; }
		/// <summary>
		/// "IL with C#" (created with the other decompilers)
		/// </summary>
		public IDecompiler? ILWithCSharp { get; set; }
#if DEBUG
		/// <summary>
		/// The ILAst views (debug builds)
		/// </summary>
		public IReadOnlyList<UpstreamILAstLanguage> ILAstLanguages { get; }
#endif

		[ImportingConstructor]
		UpstreamLanguageService(ISettingsService settingsService) {
			this.settingsService = settingsService;
			Settings = new UpstreamDecompilerSettings();
			Load();
			if (MigrateLegacySettings())
				Save();
			Settings.VersionChanged += (s, e) => Save();
			Language = new UpstreamCSharpLanguage(Settings);
#if DEBUG
			ILAstLanguages = UpstreamILAstLanguage.Create(Language).ToArray();
#endif
		}

		void Load() {
			disableSave = true;
			try {
				var section = settingsService.GetOrCreateSection(SETTINGS_GUID);
				foreach (var info in UpstreamDecompilerSettings.AllSettings) {
					if (section.Attribute<bool?>(info.Name) is bool value)
						info.SetValue(Settings, value);
				}
			}
			finally {
				disableSave = false;
			}
		}

		/// <summary>
		/// The settings of the C# decompiler this one replaced (dnSpy's fork of ILSpy 2.x)
		/// </summary>
		static readonly Guid LEGACY_SETTINGS_GUID = new Guid("6745457F-254B-4B7B-90F1-F948F0721C3B");

		/// <summary>
		/// The old C# decompiler's settings that this one also has, and their default values in the old decompiler
		/// </summary>
		static readonly (string oldName, string newName, bool oldDefault)[] legacySettings = {
			("AnonymousMethods", nameof(ICSharpCode.Decompiler.DecompilerSettings.AnonymousMethods), true),
			("ExpressionTrees", nameof(ICSharpCode.Decompiler.DecompilerSettings.ExpressionTrees), true),
			("YieldReturn", nameof(ICSharpCode.Decompiler.DecompilerSettings.YieldReturn), true),
			("AsyncAwait", nameof(ICSharpCode.Decompiler.DecompilerSettings.AsyncAwait), true),
			("QueryExpressions", nameof(ICSharpCode.Decompiler.DecompilerSettings.QueryExpressions), true),
			("UseDebugSymbols", nameof(ICSharpCode.Decompiler.DecompilerSettings.UseDebugSymbols), true),
			("ShowXmlDocumentation", nameof(ICSharpCode.Decompiler.DecompilerSettings.ShowXmlDocumentation), true),
			("SortCustomAttributes", nameof(ICSharpCode.Decompiler.DecompilerSettings.SortCustomAttributes), false),
			("ShowTokenAndRvaComments", nameof(UpstreamDecompilerSettings.ShowTokenAndRvaComments), true),
			("ForceShowAllMembers", nameof(UpstreamDecompilerSettings.ShowHiddenMembers), false),
		};

		/// <summary>
		/// Once: the settings the user changed in the old C# decompiler, unless this decompiler's settings have been
		/// changed already. The old settings are removed.
		/// </summary>
		/// <returns>true if a setting changed</returns>
		bool MigrateLegacySettings() {
			var legacyName = LEGACY_SETTINGS_GUID.ToString();
			var legacy = settingsService.Sections.FirstOrDefault(a => a.Name == legacyName);
			if (legacy is null)
				return false;
			bool changed = false;
			if (settingsService.GetOrCreateSection(SETTINGS_GUID).Attributes.Length == 0) {
				foreach (var (oldName, newName, oldDefault) in legacySettings) {
					if (!(legacy.Attribute<bool?>(oldName) is bool value) || value == oldDefault)
						continue;
					var info = UpstreamDecompilerSettings.AllSettings.FirstOrDefault(a => a.Name == newName);
					if (info is null || info.GetValue(Settings) == value)
						continue;
					disableSave = true;
					try {
						info.SetValue(Settings, value);
					}
					finally {
						disableSave = false;
					}
					changed = true;
				}
			}
			settingsService.RemoveSection(LEGACY_SETTINGS_GUID);
			return changed;
		}

		void Save() {
			if (disableSave)
				return;
			// Only the settings the user changed: a later version of the upstream decompiler can change defaults
			var section = settingsService.RecreateSection(SETTINGS_GUID);
			foreach (var info in UpstreamDecompilerSettings.AllSettings) {
				bool value = info.GetValue(Settings);
				if (value != info.DefaultValue)
					section.Attribute(info.Name, value);
			}
		}

		/// <summary>
		/// Visible tabs that show this decompiler's output (C# and "IL with C#")
		/// </summary>
		public IDocumentTab[] GetTabs(IDocumentTabService documentTabService) =>
			documentTabService.VisibleFirstTabs.Where(t => (t.Content as IDecompilerTabContent)?.Decompiler is IDecompiler d && (d == Language || d == ILWithCSharp)).ToArray();
	}

	[Export(typeof(IDecompilerCreator))]
	sealed class UpstreamDecompilerCreator : IDecompilerCreator {
		readonly UpstreamLanguageService service;
		readonly DecompilerSettingsService decompilerSettingsService;

		[ImportingConstructor]
		UpstreamDecompilerCreator(UpstreamLanguageService service, DecompilerSettingsService decompilerSettingsService) {
			this.service = service;
			this.decompilerSettingsService = decompilerSettingsService;
		}

		public IEnumerable<IDecompiler> Create() {
			yield return service.Language;
			// "IL with C#": dnSpy's IL with this decompiler's C# code
			yield return service.ILWithCSharp ??= ILWithCSharpDecompiler.Create(decompilerSettingsService.ILDecompilerSettings, service.Language);
#if DEBUG
			foreach (var ilast in service.ILAstLanguages)
				yield return ilast;
#endif
		}
	}

	/// <summary>
	/// Tells the decompiler about edited, added and removed modules
	/// </summary>
	[ExportAutoLoaded]
	sealed class DocumentListener : IAutoLoaded {
		readonly UpstreamLanguageService service;

		[ImportingConstructor]
		DocumentListener(UpstreamLanguageService service, IDocumentTabService documentTabService) {
			this.service = service;
			documentTabService.DocumentModified += DocumentTabService_DocumentModified;
			documentTabService.DocumentCollectionChanged += DocumentTabService_DocumentCollectionChanged;
		}

		void DocumentTabService_DocumentModified(object? sender, DocumentModifiedEventArgs e) {
			var modules = e.Documents.Select(a => a.ModuleDef).OfType<ModuleDef>().Distinct().ToArray();
			if (modules.Length == 0)
				return;
			// (dnSpy raises the event before it refreshes the tabs, so they don't use the old image)
			service.Language.OnModulesModified(modules);
		}

		void DocumentTabService_DocumentCollectionChanged(object? sender, NotifyDocumentCollectionChangedEventArgs e) {
			switch (e.Type) {
			case NotifyDocumentCollectionType.Add:
				service.Language.OnModulesAdded();
				break;
			case NotifyDocumentCollectionType.Remove:
				service.Language.OnModulesRemoved(e.Documents.Select(a => a.ModuleDef).OfType<ModuleDef>());
				break;
			case NotifyDocumentCollectionType.Clear:
				service.Language.OnAllModulesRemoved();
				break;
			}
		}
	}

	/// <summary>
	/// "Reload all" doesn't raise the collection events for documents that are reloaded
	/// </summary>
	[ExportDocumentListListener]
	sealed class UpstreamDocumentListListener : IDocumentListListener {
		readonly UpstreamLanguageService service;

		[ImportingConstructor]
		UpstreamDocumentListListener(UpstreamLanguageService service) => this.service = service;

		public bool CanLoad => true;
		public bool CanReload => true;
		public void BeforeLoad(bool isReload) { }
		public void AfterLoad(bool isReload) => service.Language.OnAllModulesRemoved();
		public bool CheckCanLoad(bool isReload) => true;
	}
}
