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
using System.Collections;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.CallStack;
using dnSpy.Contracts.Debugger.Evaluation;
using dnSpy.Contracts.Debugger.Text;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Text.Editor;
using Microsoft.VisualStudio.Text;

namespace dnSpy.UITests.Probe {
	/// <summary>
	/// The probe's commands. They only read what dnSpy shows, or do what a user could do with the mouse and keyboard
	/// (open a file, go to a member, put the caret on some text, select a call stack frame): the tests do the actions
	/// under test (Edit Method, stepping, export, ...) with the real UI.
	/// </summary>
	sealed class Commands {
		static readonly TimeSpan uiTimeout = TimeSpan.FromSeconds(60);
		readonly Services services;
		readonly Dispatcher dispatcher;

		public Commands(Services services, Dispatcher dispatcher) {
			this.services = services;
			this.dispatcher = dispatcher;
		}

		public JsonNode? Execute(string cmd, JsonObject args) {
			switch (cmd) {
			case "ping": return new JsonObject { ["pid"] = Environment.ProcessId };
			case "errors": return new JsonArray(Errors.Take().Select(a => (JsonNode)a).ToArray());
			case "idle": UI(() => 0, DispatcherPriority.ApplicationIdle); return null;
			// (An unobserved task exception is only raised when the GC finalizes the task)
			case "gc": GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); GC.WaitForPendingFinalizers(); return null;
			case "open": return UI(() => Open(Str(args, "path")));
			case "documents": return UI(Documents);
			case "closeAll": UI(CloseAll); return null;
			case "follow": return UI(() => Follow(Str(args, "path"), Str(args, "member"), (bool?)args["newTab"] ?? false));
			case "tab": return UI(Tab);
			case "caretTo": return UI(() => CaretTo(Str(args, "text"), (int?)args["nth"] ?? 1, (int?)args["delta"] ?? 0));
			case "languages": return UI(() => new JsonArray(services.DecompilerService.AllDecompilers.Select(a => (JsonNode)a.UniqueNameUI).ToArray()));
			case "setLanguage": return UI(() => SetLanguage(Str(args, "name")));
			case "windows": return UI(Windows);
			case "editCode": return UI(EditCode);
			case "editCodeReplace": return UI(() => EditCodeReplace(Str(args, "find"), Str(args, "replace"), (string?)args["document"] ?? "main.cs"));
			case "dbg": return DebuggerState();
			case "setFrame": return UI(() => SwitchToFrame((int)args["index"]!));
			case "eval": return Evaluate(Str(args, "expr"), (int?)args["frame"]);
			case "evalAsync": return EvaluateAsync(Str(args, "expr"));
			case "locals": return Locals((int?)args["frame"]);
			case "key": return UI(() => (JsonNode)Input.PressKey(Str(args, "keys")));
			case "click": return UI(() => (JsonNode)Input.Click(Str(args, "window"), Str(args, "button")));
			case "setText": UI(() => Input.SetText(Str(args, "window"), Str(args, "name"), Str(args, "text"))); return null;
			case "setChecked": UI(() => Input.SetChecked(Str(args, "window"), Str(args, "name"), (bool)args["value"]!)); return null;
			case "cancelDialog": return UI(() => (JsonNode)Input.CancelDialog());
			case "isChecked": return UI(() => (JsonNode?)Input.IsChecked(Str(args, "window"), Str(args, "name")));
			case "menuStep": return UI(() => (JsonNode)Input.MenuStep(args["path"]!.AsArray().Select(a => (string)a!).ToArray(), (int)args["level"]!));
			case "screenshot": return UI(() => new JsonArray(Input.Screenshot(Str(args, "path")).Select(a => (JsonNode)a).ToArray()));
			case "breakpoints": return UI(Breakpoints);
			case "clearBreakpoints": UI(() => services.BreakpointsService.Clear()); return null;
			default: throw new ProbeException($"Unknown command '{cmd}'");
			}
		}

		static string Str(JsonObject args, string name) => (string?)args[name] ?? throw new ProbeException($"Missing argument '{name}'");

		T UI<T>(Func<T> func, DispatcherPriority priority = DispatcherPriority.Normal) {
			var op = dispatcher.InvokeAsync(func, priority);
			if (op.Wait(uiTimeout) != DispatcherOperationStatus.Completed)
				throw new ProbeException("The UI thread didn't respond within " + uiTimeout.TotalSeconds + " s");
			return op.Result;
		}

		void UI(Action action) => UI(() => { action(); return 0; });

		T Dbg<T>(Func<T> func) {
			var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
			services.DbgManager.Dispatcher.BeginInvoke(() => {
				try {
					tcs.SetResult(func());
				}
				catch (Exception ex) {
					tcs.SetException(ex);
				}
			});
			if (!tcs.Task.Wait(uiTimeout))
				throw new ProbeException("The debugger thread didn't respond within " + uiTimeout.TotalSeconds + " s");
			return tcs.Task.Result;
		}

		// ---- Documents and tabs

		JsonNode Open(string path) {
			var document = services.DocumentService.TryGetOrCreate(DsDocumentInfo.CreateDocument(path));
			if (document is null)
				throw new ProbeException("Couldn't open " + path);
			return document.Filename;
		}

		JsonNode Documents() => new JsonArray(services.DocumentService.GetDocuments().Select(a => (JsonNode)a.Filename).ToArray());

		void CloseAll() {
			services.DocumentTabService.CloseAll();
			services.DocumentService.Clear();
		}

		ModuleDef GetModule(string path) {
			var full = Path.GetFullPath(path);
			// (Opened with 'open' first: the tree view adds the document's node later)
			var document = services.DocumentService.GetDocuments().FirstOrDefault(a => StringComparer.OrdinalIgnoreCase.Equals(a.Filename, full))
				?? throw new ProbeException("Not opened: " + path);
			return document.ModuleDef ?? throw new ProbeException("Not a .NET module: " + path);
		}

		/// <summary>
		/// A member: a token ("0x06000123"), a type's full name ("NS.Type", nested "NS.Type/Nested"), or a member of a
		/// type ("NS.Type::Name", the n-th overload "NS.Type::Name#2")
		/// </summary>
		static object ResolveMember(ModuleDef module, string spec) {
			if (spec == "<assembly>")
				return (object?)module.Assembly ?? module;
			if (spec == "<module>")
				return module;
			if (spec.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				return module.ResolveToken(uint.Parse(spec.Substring(2), NumberStyles.HexNumber)) as IMemberDef ?? throw new ProbeException("Bad token " + spec);
			int sep = spec.IndexOf("::", StringComparison.Ordinal);
			var typeName = sep < 0 ? spec : spec.Substring(0, sep);
			var type = module.Find(typeName, false) ?? throw new ProbeException("No type " + typeName);
			if (sep < 0)
				return type;
			var name = spec.Substring(sep + 2);
			int nth = 1;
			int hash = name.IndexOf('#');
			if (hash >= 0) {
				nth = int.Parse(name.Substring(hash + 1), CultureInfo.InvariantCulture);
				name = name.Substring(0, hash);
			}
			var members = type.Methods.Cast<IMemberDef>().Concat(type.Properties).Concat(type.Events).Concat(type.Fields).Concat(type.NestedTypes)
				.Where(a => a.Name == name).ToList();
			if (members.Count < nth)
				throw new ProbeException($"No member {name} (#{nth}) in {typeName}");
			return members[nth - 1];
		}

		JsonNode Follow(string path, string spec, bool newTab) {
			var reference = ResolveMember(GetModule(path), spec);
			services.DocumentTabService.FollowReference(reference, newTab, setFocus: true);
			return new JsonObject {
				["fullName"] = (reference as IFullName)?.FullName ?? reference.ToString(),
				["token"] = reference is IMDTokenProvider t ? "0x" + t.MDToken.Raw.ToString("X8") : null,
			};
		}

		IDocumentViewer GetViewer() =>
			services.DocumentTabService.ActiveTab.TryGetDocumentViewer() ?? throw new ProbeException("The active tab isn't a code view");

		JsonNode Tab() {
			var tab = services.DocumentTabService.ActiveTab;
			var result = new JsonObject {
				["title"] = tab?.Content.Title,
				["busy"] = tab?.IsAsyncExecInProgress ?? false,
				["language"] = services.DecompilerService.Decompiler.UniqueNameUI,
				["tabCount"] = services.DocumentTabService.SortedTabs.Count(),
			};
			var viewer = tab.TryGetDocumentViewer();
			if (viewer is not null) {
				result["text"] = viewer.TextView.TextSnapshot.GetText();
				AddCaret(result, viewer);
			}
			return result;
		}

		static void AddCaret(JsonObject result, IDocumentViewer viewer) {
			var position = viewer.Caret.Position.BufferPosition;
			var line = position.GetContainingLine();
			result["caret"] = position.Position;
			result["line"] = line.LineNumber + 1;
			result["column"] = position.Position - line.Start.Position + 1;
			result["lineText"] = line.GetText();
			var reference = viewer.SelectedReference;
			if (reference is not null)
				result["reference"] = reference.Value.Data.Reference?.ToString();
		}

		JsonNode CaretTo(string text, int nth, int delta) {
			var viewer = GetViewer();
			var all = viewer.TextView.TextSnapshot.GetText();
			int index = -1;
			for (int i = 0; i < nth; i++) {
				index = all.IndexOf(text, index + 1, StringComparison.Ordinal);
				if (index < 0)
					throw new ProbeException($"'{text}' (#{nth}) isn't in the active tab");
			}
			viewer.MoveCaretToPosition(index + delta, MoveCaretOptions.Focus | MoveCaretOptions.Center);
			var result = new JsonObject();
			AddCaret(result, viewer);
			return result;
		}

		JsonNode SetLanguage(string name) {
			var decompiler = services.DecompilerService.AllDecompilers.FirstOrDefault(a =>
				StringComparer.OrdinalIgnoreCase.Equals(a.UniqueNameUI, name) || StringComparer.OrdinalIgnoreCase.Equals(a.GenericNameUI, name))
				?? throw new ProbeException("No language " + name);
			services.DecompilerService.Decompiler = decompiler;
			return decompiler.UniqueNameUI;
		}

		// ---- Windows and dialogs

		static JsonNode Windows() {
			var array = new JsonArray();
			foreach (Window window in Application.Current.Windows) {
				if (!window.IsVisible)
					continue;
				array.Add(new JsonObject {
					["title"] = window.Title,
					["type"] = window.GetType().FullName,
					["isActive"] = window.IsActive,
					["isMain"] = window == Application.Current.MainWindow,
				});
			}
			return array;
		}

		static object GetEditCodeVM() {
			foreach (Window window in Application.Current.Windows) {
				if (window.IsVisible && window.GetType().Name == "EditCodeDlg" && window.DataContext is object vm)
					return vm;
			}
			throw new ProbeException("No Edit Code dialog");
		}

		static object? Get(object obj, string property) =>
			obj.GetType().GetProperty(property)?.GetValue(obj) ?? null;

		static JsonNode EditCode() {
			var vm = GetEditCodeVM();
			var diagnostics = new JsonArray();
			foreach (var d in (IEnumerable)Get(vm, "Diagnostics")!) {
				diagnostics.Add(new JsonObject {
					["code"] = (string?)Get(d, "Code"),
					["description"] = (string?)Get(d, "Description"),
					["file"] = (string?)Get(d, "File"),
					["line"] = (string?)Get(d, "Line"),
				});
			}
			var documents = new JsonArray();
			foreach (var d in (IEnumerable)Get(vm, "Documents")!) {
				var view = (IDsWpfTextView)Get(d, "TextView")!;
				documents.Add(new JsonObject { ["name"] = (string?)Get(d, "Name"), ["text"] = view.TextSnapshot.GetText() });
			}
			return new JsonObject {
				["hasDecompiled"] = (bool)Get(vm, "HasDecompiled")!,
				["canCompile"] = (bool)Get(vm, "CanCompile")!,
				["diagnostics"] = diagnostics,
				["documents"] = documents,
			};
		}

		static JsonNode EditCodeReplace(string find, string replace, string documentName) {
			var vm = GetEditCodeVM();
			foreach (var d in (IEnumerable)Get(vm, "Documents")!) {
				if ((string?)Get(d, "Name") != documentName)
					continue;
				var buffer = ((IDsWpfTextView)Get(d, "TextView")!).TextBuffer;
				var text = buffer.CurrentSnapshot.GetText();
				int index = text.IndexOf(find, StringComparison.Ordinal);
				if (index < 0)
					throw new ProbeException($"'{find}' isn't in {documentName}");
				buffer.Replace(new Span(index, find.Length), replace);
				return true;
			}
			throw new ProbeException("No document " + documentName);
		}

		// ---- Debugger

		JsonNode DebuggerState() {
			var (state, frames, activeFrame) = UI(() => {
				var manager = services.DbgManager;
				var process = manager.CurrentProcess.Current;
				var info = services.CallStackService.Frames;
				var state = new JsonObject {
					["isDebugging"] = manager.IsDebugging,
					["isRunning"] = manager.IsRunning,
					["processState"] = process?.State.ToString(),
					["processName"] = process?.Name,
					["pauses"] = System.Threading.Volatile.Read(ref Counters.Pauses),
				};
				var tab = services.DocumentTabService.ActiveTab;
				var viewer = tab.TryGetDocumentViewer();
				if (viewer is not null) {
					var caret = new JsonObject { ["title"] = tab!.Content.Title };
					AddCaret(caret, viewer);
					state["tab"] = caret;
				}
				return (state, info.Frames.ToArray(), info.ActiveFrameIndex);
			});
			state["activeFrame"] = activeFrame;
			var frameArray = new JsonArray();
			if (frames.Length != 0 && state["processState"]?.ToString() == nameof(DbgProcessState.Paused)) {
				var formatted = Dbg(() => frames.Select(FormatFrame).ToArray());
				for (int i = 0; i < frames.Length; i++) {
					frameArray.Add(new JsonObject {
						["index"] = i,
						["text"] = formatted[i],
						["token"] = "0x" + frames[i].FunctionToken.ToString("X8"),
						["offset"] = frames[i].FunctionOffset,
						["module"] = frames[i].Module?.Name,
					});
				}
			}
			state["frames"] = frameArray;
			return state;
		}

		/// <summary>
		/// What a double-click on a frame in the Call Stack window does (CallStackOperations.SwitchToFrame)
		/// </summary>
		JsonNode SwitchToFrame(int index) {
			var frames = services.CallStackService.Frames.Frames;
			if ((uint)index >= (uint)frames.Count)
				throw new ProbeException("No frame " + index);
			services.CallStackService.ActiveFrameIndex = index;
			services.ReferenceNavigatorService.GoTo(frames[index].Location, Array.Empty<object>());
			return true;
		}

		JsonNode Breakpoints() {
			var array = new JsonArray();
			foreach (var bp in services.BreakpointsService.Breakpoints) {
				array.Add(new JsonObject {
					["id"] = bp.Id,
					["location"] = bp.Location.ToString(),
					["enabled"] = bp.IsEnabled,
					["bound"] = bp.BoundBreakpoints.Length,
					["message"] = bp.BoundBreakpointsMessage.Message,
				});
			}
			return array;
		}

		string FormatFrame(DbgStackFrame frame) {
			var language = services.LanguageService.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid);
			var context = language.CreateContext(frame.Runtime, frame.Location);
			try {
				var sb = new StringBuilder();
				var options = DbgStackFrameFormatterOptions.ModuleNames | DbgStackFrameFormatterOptions.ParameterTypes | DbgStackFrameFormatterOptions.DeclaringTypes |
					DbgStackFrameFormatterOptions.Namespaces | DbgStackFrameFormatterOptions.IntrinsicTypeKeywords;
				language.Formatter.FormatFrame(new DbgEvaluationInfo(context, frame), new DbgStringBuilderTextWriter(sb), options, DbgValueFormatterOptions.None, CultureInfo.InvariantCulture);
				return sb.ToString();
			}
			finally {
				context.Close();
			}
		}

		(DbgStackFrame frame, DbgLanguage language) GetFrame(int? index) =>
			UI(() => {
				var info = services.CallStackService.Frames;
				int i = index ?? info.ActiveFrameIndex;
				if ((uint)i >= (uint)info.Frames.Count)
					throw new ProbeException("No frame " + i + " (is the process paused?)");
				var frame = info.Frames[i];
				return (frame, services.LanguageService.GetCurrentLanguage(frame.Runtime.RuntimeKindGuid));
			});

		JsonNode Evaluate(string expression, int? frameIndex) {
			var (frame, language) = GetFrame(frameIndex);
			return Dbg(() => {
				var context = language.CreateContext(frame.Runtime, frame.Location, DbgEvaluationContextOptions.None, TimeSpan.FromSeconds(10));
				try {
					var evalInfo = new DbgEvaluationInfo(context, frame);
					var evaluator = language.ExpressionEvaluator;
					var result = evaluator.Evaluate(evalInfo, expression, DbgEvaluationOptions.Expression, evaluator.CreateExpressionEvaluatorState());
					if (result.Error is not null)
						return new JsonObject { ["error"] = result.Error };
					var value = result.Value!;
					try {
						return new JsonObject {
							["value"] = FormatValue(language, evalInfo, value),
							["type"] = FormatType(language, evalInfo, value),
							["thrown"] = result.IsThrownException,
						};
					}
					finally {
						value.Close();
					}
				}
				finally {
					context.Close();
				}
			});
		}

		/// <summary>
		/// Starts evaluating on the debugger thread and returns at once, like a tool window (the Locals window right
		/// after a break)
		/// </summary>
		JsonNode EvaluateAsync(string expression) {
			var (frame, language) = GetFrame(null);
			services.DbgManager.Dispatcher.BeginInvoke(() => {
				var context = language.CreateContext(frame.Runtime, frame.Location, DbgEvaluationContextOptions.None, TimeSpan.FromSeconds(10));
				try {
					var evaluator = language.ExpressionEvaluator;
					var result = evaluator.Evaluate(new DbgEvaluationInfo(context, frame), expression, DbgEvaluationOptions.Expression, evaluator.CreateExpressionEvaluatorState());
					result.Value?.Close();
				}
				finally {
					context.Close();
				}
			});
			return true;
		}

				JsonNode Locals(int? frameIndex) {
			var (frame, language) = GetFrame(frameIndex);
			return Dbg(() => {
				var context = language.CreateContext(frame.Runtime, frame.Location, DbgEvaluationContextOptions.None, TimeSpan.FromSeconds(10));
				try {
					var evalInfo = new DbgEvaluationInfo(context, frame);
					var nodes = language.LocalsProvider.GetNodes(evalInfo, DbgValueNodeEvaluationOptions.None, DbgLocalsValueNodeEvaluationOptions.None);
					var array = new JsonArray();
					foreach (var info in nodes) {
						var node = info.ValueNode;
						array.Add(new JsonObject {
							["name"] = node.Expression,
							// (No calls in the process: ToString() can't run at some points, e.g. the start of a catch block,
							// and a call that times out can make the next evaluations fail)
							["value"] = node.Value is DbgValue v ? FormatValue(language, evalInfo, v, callMethods: false) : null,
							["error"] = node.ErrorMessage,
						});
						node.Close(services.DbgManager.Dispatcher);
					}
					return array;
				}
				finally {
					context.Close();
				}
			});
		}

		static string FormatValue(DbgLanguage language, DbgEvaluationInfo evalInfo, DbgValue value, bool callMethods = true) {
			var sb = new StringBuilder();
			var options = DbgValueFormatterOptions.Decimal;
			if (callMethods)
				options |= DbgValueFormatterOptions.FuncEval | DbgValueFormatterOptions.ToString;
			language.Formatter.FormatValue(evalInfo, new DbgStringBuilderTextWriter(sb), value, options, CultureInfo.InvariantCulture);
			return sb.ToString();
		}

		static string FormatType(DbgLanguage language, DbgEvaluationInfo evalInfo, DbgValue value) {
			var sb = new StringBuilder();
			language.Formatter.FormatType(evalInfo, new DbgStringBuilderTextWriter(sb), value, DbgValueFormatterTypeOptions.Decimal | DbgValueFormatterTypeOptions.Namespaces | DbgValueFormatterTypeOptions.IntrinsicTypeKeywords, CultureInfo.InvariantCulture);
			return sb.ToString();
		}
	}
}
