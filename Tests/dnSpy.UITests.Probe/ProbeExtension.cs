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
using System.Threading.Tasks;
using System.Windows;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.Breakpoints.Code;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Extension;

namespace dnSpy.UITests.Probe {
	/// <summary>
	/// Starts the probe's pipe server when dnSpy has loaded. Does nothing unless the tests started dnSpy
	/// (DNSPY_UITEST_PIPE is set).
	/// </summary>
	[ExportExtension]
	sealed class ProbeExtension : IExtension {
		public const string PipeVariable = "DNSPY_UITEST_PIPE";

		readonly Services services;
		ProbeServer? server;

		[ImportingConstructor]
		ProbeExtension(Lazy<IDocumentTabService> documentTabService, Lazy<IDsDocumentService> documentService, Lazy<IDecompilerService> decompilerService,
			Lazy<DbgManager> dbgManager, Lazy<DbgCallStackService> callStackService, Lazy<DbgLanguageService> languageService, Lazy<DbgCodeBreakpointsService> breakpointsService, Lazy<ReferenceNavigatorService> referenceNavigatorService) =>
			services = new Services(documentTabService, documentService, decompilerService, dbgManager, callStackService, languageService, breakpointsService, referenceNavigatorService);

		public IEnumerable<string> MergedResourceDictionaries => Array.Empty<string>();

		public ExtensionInfo ExtensionInfo => new ExtensionInfo { ShortDescription = "dnSpy UI tests probe" };

		public void OnEvent(ExtensionEvent @event, object? obj) {
			if (@event == ExtensionEvent.Loaded)
				Errors.Install();
			else if (@event == ExtensionEvent.AppLoaded) {
				var pipeName = Environment.GetEnvironmentVariable(PipeVariable);
				if (string.IsNullOrEmpty(pipeName))
					return;
				services.DbgManager.ProcessPaused += (s, e) => System.Threading.Interlocked.Increment(ref Counters.Pauses);
				server = new ProbeServer(pipeName, new Commands(services, Application.Current.Dispatcher));
				server.Start();
			}
			else if (@event == ExtensionEvent.AppExit)
				server?.Dispose();
		}
	}

	sealed class Services {
		readonly Lazy<IDocumentTabService> documentTabService;
		readonly Lazy<IDsDocumentService> documentService;
		readonly Lazy<IDecompilerService> decompilerService;
		readonly Lazy<DbgManager> dbgManager;
		readonly Lazy<DbgCallStackService> callStackService;
		readonly Lazy<DbgLanguageService> languageService;
		readonly Lazy<DbgCodeBreakpointsService> breakpointsService;
		readonly Lazy<ReferenceNavigatorService> referenceNavigatorService;

		public IDocumentTabService DocumentTabService => documentTabService.Value;
		public IDsDocumentService DocumentService => documentService.Value;
		public IDecompilerService DecompilerService => decompilerService.Value;
		public DbgManager DbgManager => dbgManager.Value;
		public DbgCallStackService CallStackService => callStackService.Value;
		public DbgLanguageService LanguageService => languageService.Value;
		public DbgCodeBreakpointsService BreakpointsService => breakpointsService.Value;
		public ReferenceNavigatorService ReferenceNavigatorService => referenceNavigatorService.Value;

		public Services(Lazy<IDocumentTabService> documentTabService, Lazy<IDsDocumentService> documentService, Lazy<IDecompilerService> decompilerService,
			Lazy<DbgManager> dbgManager, Lazy<DbgCallStackService> callStackService, Lazy<DbgLanguageService> languageService, Lazy<DbgCodeBreakpointsService> breakpointsService, Lazy<ReferenceNavigatorService> referenceNavigatorService) {
			this.documentTabService = documentTabService;
			this.documentService = documentService;
			this.decompilerService = decompilerService;
			this.dbgManager = dbgManager;
			this.callStackService = callStackService;
			this.languageService = languageService;
			this.breakpointsService = breakpointsService;
			this.referenceNavigatorService = referenceNavigatorService;
		}
	}

	static class Counters {
		/// <summary>
		/// How many times a debugged process paused (a breakpoint hit twice in a row looks the same otherwise)
		/// </summary>
		public static int Pauses;
	}

	/// <summary>
	/// Exceptions that reached dnSpy's top level (they'd otherwise only show as a message box or a crash)
	/// </summary>
	static class Errors {
		static readonly List<string> errors = new List<string>();
		static bool installed;

		public static void Install() {
			if (installed)
				return;
			installed = true;
			AppDomain.CurrentDomain.UnhandledException += (s, e) => Add("AppDomain", e.ExceptionObject);
			TaskScheduler.UnobservedTaskException += (s, e) => Add("UnobservedTask", e.Exception);
			Application.Current.DispatcherUnhandledException += (s, e) => Add("Dispatcher", e.Exception);
		}

		static void Add(string source, object? exception) {
			lock (errors)
				errors.Add($"{source}: {exception}");
		}

		public static string[] Take() {
			lock (errors) {
				var result = errors.ToArray();
				errors.Clear();
				return result;
			}
		}
	}
}
