using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.Text.Classification;
using Microsoft.VisualStudio.Text.Classification;

namespace dnSpy.StringSearcher {
	public interface IStringReferencesService : IUIObjectProvider {
		StringReference? CurrentReference { get; }

		void Analyze(IEnumerable<ModuleDef> modules);
		void Analyze(ModuleDef module);
		void Refresh();
		void FollowReference(StringReference reference, bool newTab);
	}

	[Export(typeof(IStringReferencesService))]
	public class StringReferencesService : IStringReferencesService {
		private readonly IDecompilerService decompilerService;
		private readonly ITextElementProvider textElementProvider;
		private readonly IClassificationFormatMapService classificationFormatMapService;
		private readonly IDocumentTabService documentTabService;
		private readonly Action<IEnumerable<StringReference>> addItems;
		private readonly StringsControlVM vm;
		private readonly Dispatcher dispatcher;
		private ModuleDef[] selectedModules = [];

		public StringsControl UIObject { get; }

		object IUIObjectProvider.UIObject => UIObject;

		public IInputElement? FocusedElement => null;

		public FrameworkElement? ZoomElement => UIObject.ListView;

		public StringReference? CurrentReference => vm.SelectedStringReference;

		[ImportingConstructor]
		public StringReferencesService(
			IDecompilerService decompilerService,
			ITextElementProvider textElementProvider,
			IClassificationFormatMapService classificationFormatMapService,
			IDocumentTabService documentTabService,
			IMenuService menuService,
			IWpfCommandService wpfCommandService) {

			this.decompilerService = decompilerService;
			this.textElementProvider = textElementProvider;
			this.classificationFormatMapService = classificationFormatMapService;
			this.documentTabService = documentTabService;

			UIObject = new StringsControl {
				DataContext = vm = new StringsControlVM(this)
			};

			dispatcher = Dispatcher.CurrentDispatcher;

			addItems = items => {
				foreach (var item in items) {
					vm.StringLiterals.Add(item);
				}
			};

			menuService.InitializeContextMenu(
				UIObject.ListView,
				new Guid(StringSearcherConstants.GUID_STRINGS_LISTBOX),
				new GuidObjectsProvider()
			);

			wpfCommandService.Add(new Guid(StringSearcherConstants.GUID_STRINGS_LISTBOX), UIObject.ListView);

			UIObject.ListView.MouseDoubleClick += (_, _) => {
				FollowSelectedReference((Keyboard.Modifiers & ModifierKeys.Control) != 0);
			};
			UIObject.ListView.KeyUp += (_, e) => {
				if (e.Key == Key.Enter) {
					FollowSelectedReference((Keyboard.Modifiers & ModifierKeys.Control) != 0);
				}
			};
		}

		private void FollowSelectedReference(bool newTab) {
			if (vm.SelectedStringReference is { } selected) {
				FollowReference(selected, newTab);
			}
		}

		public void Analyze(IEnumerable<ModuleDef> modules) {
			selectedModules = modules.ToArray();
			AnalyzeSelectedModules();
		}

		public void Analyze(ModuleDef module) => Analyze([module]);

		private void AnalyzeSelectedModules() {
			var context = new StringReferenceContext(
				decompilerService.Decompiler,
				textElementProvider,
				classificationFormatMapService.GetClassificationFormatMap("UIMisc") // TODO: replace string with AppearanceCategoryConstants.UIMisc
			);

			vm.StringLiterals.Clear();

			Task.Factory.StartNew(() => {
				Parallel.ForEach(selectedModules.SelectMany(x => x.GetTypes()), type => {
					var items = new List<StringReference>();

					if (type.HasFields)
						AnalyzeConstantProviders(context, type.Fields, items);

					if (type.HasProperties)
						AnalyzeConstantProviders(context, type.Properties, items);

					foreach (var method in type.Methods) {
						if (method.HasParamDefs)
							AnalyzeConstantProviders(context, method.ParamDefs, items);
						if (method.HasBody && method.Body is { HasInstructions: true })
							AnalyzeBody(context, method, items);
					}

					if (items.Count > 0) {
						dispatcher.BeginInvoke(addItems, [items]);
					}
				});
			});
		}

		private static void AnalyzeConstantProviders(StringReferenceContext context, IEnumerable<IHasConstant> items, List<StringReference> result) {
			foreach (var item in items) {
				if (item.Constant is { Type: ElementType.String, Value: string { Length: > 0 } value }) {
					result.Add(new ConstantStringReference(context, value, item));
				}
			}
		}

		private static void AnalyzeBody(StringReferenceContext context, MethodDef method, List<StringReference> items) {
			foreach (var instruction in method.Body.Instructions) {
				if (instruction is { OpCode.Code: Code.Ldstr, Operand: string { Length: > 0 } operand }) {
					items.Add(new MethodBodyStringReference(context, operand, method, instruction.Offset));
				}
			}
		}

		public void Refresh() => AnalyzeSelectedModules();

		public void FollowReference(StringReference reference, bool newTab) {
			documentTabService.FollowReference(reference.Referrer, newTab, true, a => {
				if (a.HasMovedCaret || !a.Success) {
					return;
				}

				switch (reference) {
				case MethodBodyStringReference methodString:
					a.HasMovedCaret = GoTo(a.Tab, methodString.Referrer, methodString.Offset);
					break;
				}
			});
		}

		private static bool GoTo(IDocumentTab tab, MethodDef method, uint ilOffset) {
			if (tab.TryGetDocumentViewer() is { } documentViewer
				&& documentViewer.GetMethodDebugService().FindByCodeOffset(method, ilOffset) is { } methodStatement) {
				documentViewer.MoveCaretToPosition(methodStatement.Statement.TextSpan.Start);
				return true;
			}

			return false;
		}

		private sealed class GuidObjectsProvider : IGuidObjectsProvider {
			public IEnumerable<GuidObject> GetGuidObjects(GuidObjectsProviderArgs args) {
				if (args.CreatorObject.Object is ListView { SelectedItem: StringReference stringReference }) {
					yield return new GuidObject(
						new Guid(StringSearcherConstants.GUID_STRING_REFERENCE),
						stringReference
					);
				}
			}
		}
	}
}
