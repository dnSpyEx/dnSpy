using System;
using System.ComponentModel.Composition;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using dnlib.DotNet;
using dnSpy.AsmEditor.Commands;
using dnSpy.AsmEditor.Properties;
using dnSpy.AsmEditor.UndoRedo;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Menus;
using dnSpy.Contracts.MVVM;

namespace dnSpy.AsmEditor.Compiler {
	[DebuggerDisplay("{Description}")]
	sealed class LoadDependenciesCommand : EditCodeCommandBase {
		[ExportMenuItem(Header = "res:LoadDependenciesCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_ILED, Order = 28.999)]
		sealed class DocumentsCommand : DocumentsContextMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider;
			readonly IAppService appService;

			[ImportingConstructor]
			DocumentsCommand(Lazy<IUndoCommandService> undoCommandService, Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider, IAppService appService) {
				this.undoCommandService = undoCommandService;
				this.addUpdatedNodesHelperProvider = addUpdatedNodesHelperProvider;
				this.appService = appService;
			}

			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommand.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommand.Execute(addUpdatedNodesHelperProvider, undoCommandService, appService, context.Nodes);
		}

		[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:LoadDependenciesCommand", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_SETTINGS, Order = 58.999)]
		sealed class EditMenuCommand : EditMenuHandler {
			readonly Lazy<IUndoCommandService> undoCommandService;
			readonly Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider;
			readonly IAppService appService;

			[ImportingConstructor]
			EditMenuCommand(Lazy<IUndoCommandService> undoCommandService, Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider, IAppService appService)
				: base(appService.DocumentTreeView) {
				this.undoCommandService = undoCommandService;
				this.addUpdatedNodesHelperProvider = addUpdatedNodesHelperProvider;
				this.appService = appService;
			}

			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommand.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommand.Execute(addUpdatedNodesHelperProvider, undoCommandService, appService, context.Nodes);
		}

		static bool CanExecute(DocumentTreeNodeData[] nodes) => nodes.Length == 1 && GetModuleNode(nodes[0]) is not null;

		static ModuleDocumentNode? GetModuleNode(DocumentTreeNodeData node) {
			if (node is AssemblyDocumentNode asmNode) {
				asmNode.TreeNode.EnsureChildrenLoaded();
				return asmNode.TreeNode.DataChildren.FirstOrDefault() as ModuleDocumentNode;
			}
			else
				return node.GetModuleNode();
		}

		static void Execute(Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider, Lazy<IUndoCommandService> undoCommandService, IAppService appService, DocumentTreeNodeData[] nodes) {
			if (!CanExecute(nodes))
				return;

			var modNode = GetModuleNode(nodes[0]);
			Debug2.Assert(modNode is not null);
			if (modNode is null)
				return;

			var module = modNode.Document.ModuleDef;
			Debug2.Assert(module is not null);
			if (module is null)
				throw new InvalidOperationException();

			foreach (var assemblyRef in module.GetAssemblyRefs()) {
				modNode.Context.DocumentTreeView.DocumentService.Resolve(assemblyRef, module);
			}
		}

		readonly struct ModuleResult {
			public IAssembly? Assembly { get; }
			public byte[] RawBytes { get; }
			public DebugFileResult DebugFile { get; }
			public ModuleResult(IAssembly? assembly, byte[] bytes, DebugFileResult debugFile) {
				Assembly = assembly;
				RawBytes = bytes;
				DebugFile = debugFile;
			}
		}

		LoadDependenciesCommand(Lazy<IAddUpdatedNodesHelperProvider> addUpdatedNodesHelperProvider, ModuleDocumentNode modNode, ModuleImporter importer)
			: base(addUpdatedNodesHelperProvider, modNode, importer) {
		}

		public override string Description => dnSpy_AsmEditor_Resources.LoadDependenciesCommand;
	}
}
