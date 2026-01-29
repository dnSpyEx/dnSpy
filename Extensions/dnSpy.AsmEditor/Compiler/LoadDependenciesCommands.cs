/*
    Copyright (C) 2026 ElektroKill

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
using System.Linq;
using dnlib.DotNet;
using dnSpy.AsmEditor.Commands;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Menus;

namespace dnSpy.AsmEditor.Compiler {
	sealed class LoadDependenciesCommands  {
		[ExportMenuItem(Header = "res:LoadDependenciesCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_ILED, Order = 28.999)]
		sealed class DocumentsCommand : DocumentsContextMenuHandler {
			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommands.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommands.Execute(context.Nodes, false);
		}

		[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:LoadDependenciesCommand", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_SETTINGS, Order = 58.999)]
		sealed class EditMenuCommand : EditMenuHandler {
			[ImportingConstructor]
			EditMenuCommand(IAppService appService) : base(appService.DocumentTreeView) { }

			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommands.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommands.Execute(context.Nodes, false);
		}

		[ExportMenuItem(Header = "res:LoadDependenciesRecursiveCommand", Group = MenuConstants.GROUP_CTX_DOCUMENTS_ASMED_ILED, Order = 29.999)]
		sealed class RecursiveDocumentsCommand : DocumentsContextMenuHandler {
			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommands.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommands.Execute(context.Nodes, true);
		}

		[ExportMenuItem(OwnerGuid = MenuConstants.APP_MENU_EDIT_GUID, Header = "res:LoadDependenciesRecursiveCommand", Group = MenuConstants.GROUP_APP_MENU_EDIT_ASMED_SETTINGS, Order = 59.999)]
		sealed class RecursiveEditMenuCommand : EditMenuHandler {
			[ImportingConstructor]
			RecursiveEditMenuCommand(IAppService appService) : base(appService.DocumentTreeView) { }

			public override bool IsVisible(AsmEditorContext context) => LoadDependenciesCommands.CanExecute(context.Nodes);
			public override void Execute(AsmEditorContext context) => LoadDependenciesCommands.Execute(context.Nodes, true);
		}

		static bool CanExecute(DocumentTreeNodeData[] nodes) => nodes.Length == 1 && GetModuleNode(nodes[0]) is not null;

		static ModuleDocumentNode? GetModuleNode(DocumentTreeNodeData node) {
			if (node is AssemblyDocumentNode asmNode) {
				asmNode.TreeNode.EnsureChildrenLoaded();
				return asmNode.TreeNode.DataChildren.FirstOrDefault() as ModuleDocumentNode;
			}

			return node.GetModuleNode();
		}

		static void Execute(DocumentTreeNodeData[] nodes, bool recursive) {
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

			var documentService = modNode.Context.DocumentTreeView.DocumentService;

			if (!recursive) {
				foreach (var assemblyRef in module.GetAssemblyRefs())
					documentService.Resolve(assemblyRef, module);
			}
			else {
				var processedModules = new HashSet<ModuleDef>();

				var queue = new Stack<ModuleDef>();
				queue.Push(module);

				while (queue.Count > 0) {
					var mod = queue.Pop();
					if (!processedModules.Add(mod))
						continue;

					foreach (var assemblyRef in mod.GetAssemblyRefs()) {
						var asm = documentService.Resolve(assemblyRef, mod);
						if (asm?.ModuleDef is null)
							continue;
						queue.Push(asm.ModuleDef);
					}
				}
			}
		}
	}
}
