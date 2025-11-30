/*
    Copyright (C) 2025 Washi

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
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;
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
		private static readonly List<string> CachedFixedArgumentNames = [];

		private readonly IDecompilerService decompilerService;
		private readonly ITextElementProvider textElementProvider;
		private readonly IClassificationFormatMapService classificationFormatMapService;
		private readonly IDocumentTabService documentTabService;
		private readonly IDotNetImageService dotNetImageService;
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
			IWpfCommandService wpfCommandService,
			IDotNetImageService dotNetImageService) {

			this.decompilerService = decompilerService;
			this.textElementProvider = textElementProvider;
			this.classificationFormatMapService = classificationFormatMapService;
			this.documentTabService = documentTabService;
			this.dotNetImageService = dotNetImageService;
			UIObject = new StringsControl {
				DataContext = vm = new StringsControlVM(this)
			};

			dispatcher = Dispatcher.CurrentDispatcher;

			addItems = items => {
				foreach (var item in items) {
					vm.StringLiterals.Add(item);
				}
			};

			var listboxGuid = new Guid(StringSearcherConstants.GUID_STRINGS_LISTBOX);
			menuService.InitializeContextMenu(
				UIObject.ListView,
				listboxGuid,
				new GuidObjectsProvider()
			);

			wpfCommandService.Add(listboxGuid, UIObject.ListView);

			var commands = wpfCommandService.GetCommands(listboxGuid);
			commands.Add(new RelayCommand(_ => FollowSelectedReference(false)), ModifierKeys.None, Key.Enter);
			commands.Add(new RelayCommand(_ => FollowSelectedReference(true)), ModifierKeys.Control, Key.Enter);
			commands.Add(new RelayCommand(_ => FollowSelectedReference(true)), ModifierKeys.Shift, Key.Enter);

			UIObject.ListView.MouseDoubleClick += (_, _) => {
				FollowSelectedReference((Keyboard.Modifiers & ModifierKeys.Control) != 0);
			};

			decompilerService.DecompilerChanged += (_, _) => Refresh();
			documentTabService.DocumentModified += (_, _) => Refresh();
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
				classificationFormatMapService.GetClassificationFormatMap("UIMisc"), // TODO: replace string with AppearanceCategoryConstants.UIMisc
				dotNetImageService
			);

			vm.StringLiterals.Clear();

			Task.Factory.StartNew(() => {
				AnalyzeMetadataRoots(context);
				AnalyzeModules(context);
			});
		}

		private void AnalyzeMetadataRoots(StringReferenceContext context) {
			var items = new List<StringReference>();
			foreach (var module in selectedModules) {
				var moduleContext = new ObjectContext {
					Context = context,
					Module = module,
					Container = module,
					Object = module
				};

				AnalyzeCustomAttributeProvider(moduleContext, module, items);

				if (module.IsManifestModule) {
					var assemblyContext = moduleContext with {
						Container = module.Assembly,
						Object = module.Assembly,
					};

					AnalyzeCustomAttributeProvider(assemblyContext, module.Assembly, items);
				}
			}

			if (items.Count > 0) {
				dispatcher.BeginInvoke(addItems, [items]);
			}
		}

		private void AnalyzeModules(StringReferenceContext context) {
			Parallel.ForEach(selectedModules.SelectMany(x => x.GetTypes()), type => {
				var typeContext = new ObjectContext {
					Context = context,
					Module = type.Module,
					Container = type,
					Object = type,
				};

				if (!context.Decompiler.ShowMember(type))
					return;

				var items = new List<StringReference>();

				AnalyzeCustomAttributeProvider(in typeContext, type, items);

				if (type.HasGenericParameters)
					AnalyzeConstantProviders(in typeContext, type.GenericParameters, items);

				if (type.HasFields)
					AnalyzeConstantProviders(in typeContext, type.Fields, items);

				if (type.HasProperties)
					AnalyzeConstantProviders(in typeContext, type.Properties, items);

				if (type.HasEvents)
					AnalyzeConstantProviders(in typeContext, type.Events, items);

				if (type.HasMethods) {
					foreach (var method in type.Methods) {
						if (!context.Decompiler.ShowMember(method))
							continue;

						var methodContext = typeContext with { Object = method, Container = method };

						AnalyzeCustomAttributeProvider(in methodContext, method, items);

						if (method.HasGenericParameters)
							AnalyzeConstantProviders(in methodContext, method.GenericParameters, items);

						if (method.HasParamDefs)
							AnalyzeConstantProviders(in methodContext, method.ParamDefs, items);

						if (method.HasBody && method.Body is { HasInstructions: true })
							AnalyzeBody(context, method, items);
					}
				}

				if (items.Count > 0) {
					dispatcher.BeginInvoke(addItems, [items]);
				}
			});
		}

		private static void AnalyzeConstantProviders(in ObjectContext context, IEnumerable<IMDTokenProvider> items, List<StringReference> result) {
			foreach (var item in items) {
				var itemContext = context with { Object = item };

				if (item is IMemberRef member && !context.Context.Decompiler.ShowMember(member)) {
					continue;
				}

				// Check for constants
				if (item is IHasConstant { Constant: { Type: ElementType.String, Value: string { Length: > 0 } value } } hasConstant) {
					result.Add(new ConstantStringReference(itemContext.Context, value, hasConstant));
				}

				// Check for CAs
				if (item is IHasCustomAttribute hasCustomAttribute) {
					AnalyzeCustomAttributeProvider(in itemContext, hasCustomAttribute, result);
				}
			}
		}

		private static void AnalyzeCustomAttributeProvider(in ObjectContext context, IHasCustomAttribute hasCustomAttribute, List<StringReference> result) {
			foreach (var attribute in hasCustomAttribute.GetCustomAttributes()) {
				for (int i = 0; i < attribute.ConstructorArguments.Count; i++) {
					AnalyzeCAArgument(in context, attribute, GetFixedArgumentName(i), attribute.ConstructorArguments[i].Value, result);
				}
				foreach (var argument in attribute.NamedArguments) {
					AnalyzeCAArgument(in context, attribute, argument.Name, argument.Value, result);
				}
			}

			static string GetFixedArgumentName(int index) {
				// Fast path: Most of the time the string should already be created before.
				// Note that this is safe to do outside a lock, even if the list is updated by another thread.
				// This is because the internal array only grows, and the stored reference to the internal array
				// is not updated until the new array is fully initialized, therefore always satisfying the
				// invariant (index < CachedFixedArgumentNames.Count).
				if (index < CachedFixedArgumentNames.Count) {
					return CachedFixedArgumentNames[index];
				}

				// Slow path: String has not been created yet. Add all missing arg names to cache.
				lock (CachedFixedArgumentNames) {
					while (CachedFixedArgumentNames.Count <= index) {
						CachedFixedArgumentNames.Add($"Argument #{CachedFixedArgumentNames.Count}");
					}

					return CachedFixedArgumentNames[index];
				}
			}
		}

		private static void AnalyzeCAArgument(in ObjectContext context, CustomAttribute attribute, string argumentName, object? value, List<StringReference> result) {
			switch (value) {
			case IEnumerable<CAArgument> list:
				foreach (var item in list) {
					AnalyzeCAArgument(in context, attribute, argumentName, item.Value, result);
				}
				break;

			case CAArgument argument:
				AnalyzeCAArgument(in context, attribute, argumentName, argument.Value, result);
				break;

			case UTF8String { Length: > 0 } constant:
				result.Add(new CustomAttributeStringReference(
					context.Context,
					constant,
					(IHasCustomAttribute)context.Object,
					context.Container,
					context.Module,
					attribute,
					argumentName
				));
				break;
			}
		}

		private static void AnalyzeBody(StringReferenceContext context, MethodDef method, List<StringReference> result) {
			foreach (var instruction in method.Body.Instructions) {
				if (instruction is { OpCode.Code: Code.Ldstr, Operand: string { Length: > 0 } operand }) {
					result.Add(new ILStringReference(context, operand, method, instruction.Offset));
				}
			}
		}

		public void Refresh() => AnalyzeSelectedModules();

		public void FollowReference(StringReference reference, bool newTab) {
			documentTabService.FollowReference(reference.Referrer, newTab, true, a => {
				if (a.HasMovedCaret || !a.Success) {
					return;
				}

				// Specialize for different types of references.
				switch (reference) {
				case ILStringReference ilReference:
					a.HasMovedCaret = GoTo(a.Tab, ilReference.Referrer, ilReference.Offset);
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

		private record struct ObjectContext(
			StringReferenceContext Context,
			ModuleDef Module,
			IMDTokenProvider Container,
			object Object
		);
	}
}
