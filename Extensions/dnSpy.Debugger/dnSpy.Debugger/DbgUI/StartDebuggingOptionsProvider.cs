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
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using dnlib.DotNet.MD;
using dnlib.PE;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.StartDebugging;
using dnSpy.Contracts.Debugger.StartDebugging.Dialog;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Debugger.Dialogs.DebugProgram;
using dnSpy.Debugger.Properties;

namespace dnSpy.Debugger.DbgUI {
	[Export(typeof(StartDebuggingOptionsProvider))]
	sealed class StartDebuggingOptionsProvider {
		readonly IAppWindow appWindow;
		readonly IDocumentTabService documentTabService;
		readonly Lazy<StartDebuggingOptionsPageProvider>[] startDebuggingOptionsPageProviders;
		readonly Lazy<DbgProcessStarterService> dbgProcessStarterService;
		readonly Lazy<GenericDebugEngineGuidProvider, IGenericDebugEngineGuidProviderMetadata>[] genericDebugEngineGuidProviders;
		readonly Lazy<Settings.DebuggerSettingsImpl> debuggerSettingsImpl;
		readonly StartDebuggingOptionsMru mru;

		[ImportingConstructor]
		StartDebuggingOptionsProvider(IAppWindow appWindow, IDocumentTabService documentTabService, Lazy<DbgProcessStarterService> dbgProcessStarterService, [ImportMany] IEnumerable<Lazy<StartDebuggingOptionsPageProvider>> startDebuggingOptionsPageProviders, [ImportMany] IEnumerable<Lazy<GenericDebugEngineGuidProvider, IGenericDebugEngineGuidProviderMetadata>> genericDebugEngineGuidProviders, Lazy<Settings.DebuggerSettingsImpl> debuggerSettingsImpl) {
			this.debuggerSettingsImpl = debuggerSettingsImpl;
			this.appWindow = appWindow;
			this.documentTabService = documentTabService;
			this.dbgProcessStarterService = dbgProcessStarterService;
			this.startDebuggingOptionsPageProviders = startDebuggingOptionsPageProviders.ToArray();
			this.genericDebugEngineGuidProviders = genericDebugEngineGuidProviders.OrderBy(a => a.Metadata.Order).ToArray();
			mru = new StartDebuggingOptionsMru();
		}

		StartDebuggingOptionsPage[] GetStartDebuggingOptionsPages(StartDebuggingOptionsPageContext context) {
			var list = new List<StartDebuggingOptionsPage>();
			foreach (var provider in startDebuggingOptionsPageProviders)
				list.AddRange(provider.Value.Create(context));
			return list.OrderBy(a => a.DisplayOrder).ToArray();
		}

		string GetCurrentFilename() {
			var filename = documentTabService.DocumentTreeView.TreeView.SelectedItem.GetDocumentNode()?.Document.Filename ?? string.Empty;
			if (File.Exists(filename))
				return filename;
			return string.Empty;
		}

		public string? GetCurrentExecutableFilename() {
			var filename = GetCurrentFilename();
			if (PortableExecutableFileHelpers.IsExecutable(filename))
				return filename;
			return null;
		}

		public (StartDebuggingOptions options, StartDebuggingOptionsInfoFlags flags) GetStartDebuggingOptions(string? defaultBreakKind) {
			var breakKind = defaultBreakKind ?? PredefinedBreakKinds.DontBreak;
			var filename = GetCurrentFilename();
			var context = new StartDebuggingOptionsPageContext(filename);
			var pages = GetStartDebuggingOptionsPages(context);
			Debug.Assert(pages.Length != 0, "No debug engines!");
			if (pages.Length == 0)
				return default;

			var oldOptions = mru.TryGetOptions(filename);
			var lastOptions = mru.TryGetLastOptions();
			if (oldOptions == null && lastOptions.HasValue && lastOptions!.Value.options != null && debuggerSettingsImpl.Value.DontAutoUpdateDebugLaunchSettings)
				oldOptions = lastOptions.Value;

			foreach (var page in pages) {
				if (oldOptions?.pageGuid == page.Guid)
					page.InitializePreviousOptions(WithBreakKind(oldOptions!.Value.options, defaultBreakKind));
				else if (oldOptions is null && lastOptions?.pageGuid == page.Guid)
					page.InitializeDefaultOptions(filename, breakKind, WithBreakKind(lastOptions!.Value.options, defaultBreakKind));
				else
					page.InitializeDefaultOptions(filename, breakKind, null);
			}

			// If there's an exact match ('oldOptions'), then prefer it.
			// Otherwise ask code that knows what kind of exe it is, but prefer the last selected guid if there are multiple matches.
			// Else use last page guid.
			var selectedPageGuid =
				oldOptions?.pageGuid ??
				GetDefaultPageGuid(pages, filename, lastOptions?.pageGuid) ??
				lastOptions?.pageGuid ??
				Guid.Empty;

			var dlg = new DebugProgramDlg();
			var vm = new DebugProgramVM(pages, selectedPageGuid);
			dlg.DataContext = vm;
			dlg.Owner = appWindow.MainWindow;
			var res = dlg.ShowDialog();
			vm.Close();
			if (res != true)
				return default;
			var info = vm.StartDebuggingOptions;
			if (info.Filename is not null) {
				var isCompatible = IsCompatibleWithCurrentArchitecture(info.Filename);
				string? message = null;
				if (isCompatible is null)
					message = dnSpy_Debugger_Resources.StartDebuggingWarning_UnknownBitness;
				else if (!isCompatible.Value)
					message = dnSpy_Debugger_Resources.StartDebuggingWarning_IncorrectBitness;

				if (message is not null) {
					var result = MsgBox.Instance.Show(message, MsgBoxButton.Yes | MsgBoxButton.No);
					if (result != MsgBoxButton.Yes)
						return default;
				}
			}

			mru.Add(info.Filename!, info.Options, vm.SelectedPageGuid);
			return (info.Options, info.Flags);
		}

		static bool? IsCompatibleWithCurrentArchitecture(string fileName) {
			int? bitness = null;
			try {
				using (var peImage = new PEImage(fileName, false)) {
					var machine = peImage.ImageNTHeaders.FileHeader.Machine;
					if (machine.Is64Bit())
						bitness = 64;
					else if (machine.IsI386()) {
						var dotNetDir = peImage.ImageNTHeaders.OptionalHeader.DataDirectories[14];
						bool isDotNet = dotNetDir.VirtualAddress != 0;
						if (isDotNet) {
							var cor20HeaderReader = peImage.CreateReader(dotNetDir.VirtualAddress, 0x48);
							var cor20Header = new ImageCor20Header(ref cor20HeaderReader, false);

							var version = (uint)(cor20Header.MajorRuntimeVersion << 16) | cor20Header.MinorRuntimeVersion;

							// If the runtime version is < 2.5, then it's always loaded as a 32-bit process.
							if (version < 0x00020005)
								bitness = 32;
							else {
								var bit32Required = (cor20Header.Flags & ComImageFlags.Bit32Required) != 0;
								var bit32Preferred = (cor20Header.Flags & ComImageFlags.Bit32Preferred) != 0;
								var ilOnly = (cor20Header.Flags & ComImageFlags.ILOnly) != 0;

								if (bit32Required)
									bitness = 32;
								else if (!bit32Preferred) {
									if (ilOnly)
										bitness = Environment.Is64BitOperatingSystem ? 64 : 32;
									else
										bitness = 32;
								}
							}
						}
						else
							bitness = 32;
					}
				}
			}
			catch {
				return null;
			}

			if (bitness is null)
				return null;

			return bitness == IntPtr.Size * 8;
		}

		static StartDebuggingOptions WithBreakKind(StartDebuggingOptions options, string? breakKind) {
			if (breakKind is null)
				return options;
			options = (StartDebuggingOptions)options.Clone();
			options.BreakKind = breakKind;
			return options;
		}

		Guid? GetDefaultPageGuid(StartDebuggingOptionsPage[] pages, string filename, Guid? lastGuid) {
			var engineGuids = new List<Guid>();
			foreach (var lz in genericDebugEngineGuidProviders) {
				var engineGuid = lz.Value.GetEngineGuid(filename);
				if (engineGuid is not null)
					engineGuids.Add(engineGuid.Value);
			}

			Guid? firstResult = null;
			double? firstOrder = null;
			foreach (var engineGuid in engineGuids) {
				foreach (var page in pages) {
					if (page.SupportsDebugEngine(engineGuid, out double order)) {
						// Always prefer the last used page if it matches again
						if (page.Guid == lastGuid)
							return lastGuid;

						if (firstResult is null || order < firstOrder!.Value) {
							firstResult = page.Guid;
							firstOrder = order;
						}
					}
				}
				// The order of the engine guids is important so exit as soon as we find a match
				if (firstResult is not null)
					break;
			}
			return firstResult;
		}

		public bool CanStartWithoutDebugging(out StartDebuggingResult result) => TryGetStartWithoutDebuggingInfo(out _, out result);

		bool TryGetStartWithoutDebuggingInfo(out string filename, out StartDebuggingResult result) {
			filename = GetCurrentFilename();
			result = StartDebuggingResult.None;
			if (!File.Exists(filename))
				return false;
			if (!dbgProcessStarterService.Value.CanStart(filename, out var startResult))
				return false;
			if ((startResult & ProcessStarterResult.WrongExtension) != 0)
				result |= StartDebuggingResult.WrongExtension;
			return true;
		}

		public bool StartWithoutDebugging([NotNullWhen(false)]out string? error) {
			if (!TryGetStartWithoutDebuggingInfo(out var filename, out _))
				throw new InvalidOperationException();
			return dbgProcessStarterService.Value.TryStart(filename, out error);
		}
	}

	[Flags]
	enum StartDebuggingResult {
		None			= 0,
		WrongExtension	= 0x00000001,
	}
}
