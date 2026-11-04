/*
    Copyright (C) 2023 ElektroKill
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

// The ILAst stepper of dnSpyEx (feature/new-ilspy, by ElektroKill), adapted to the upstream decompiler's ILAst view

#if DEBUG
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using dnSpy.Contracts.Controls;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents.Tabs;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Extension;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Contracts.ToolWindows;
using dnSpy.Contracts.ToolWindows.App;
using dnSpy.Contracts.TreeView;
using dnSpy.Contracts.TreeView.Text;
using dnSpy.Decompiler.ILSpy.Core.ILAst;
using ICSharpCode.Decompiler.DebugSteps;

namespace dnSpy.Decompiler.ILSpy.ILAst {
	static class StepperConstants {
		public static readonly Guid ToolWindowGuid = new Guid("64BF51D4-DB12-4263-B107-5FD195EED39B");
		public static readonly Guid TreeViewGuid = new Guid("3C9F2E71-8B4A-4D06-9E15-6A0B7C2D8F43");
	}

	[Export(typeof(IToolWindowContentProvider))]
	sealed class StepperToolWindowContentProvider : IToolWindowContentProvider {
		readonly UpstreamLanguageService service;
		readonly IDocumentTabService documentTabService;
		readonly ITreeViewService treeViewService;
		readonly ITreeViewNodeTextElementProvider treeViewNodeTextElementProvider;
		StepperToolWindowContent? content;

		[ImportingConstructor]
		StepperToolWindowContentProvider(UpstreamLanguageService service, IDocumentTabService documentTabService, ITreeViewService treeViewService, ITreeViewNodeTextElementProvider treeViewNodeTextElementProvider) {
			this.service = service;
			this.documentTabService = documentTabService;
			this.treeViewService = treeViewService;
			this.treeViewNodeTextElementProvider = treeViewNodeTextElementProvider;
		}

		public IEnumerable<ToolWindowContentInfo> ContentInfos {
			get { yield return new ToolWindowContentInfo(StepperConstants.ToolWindowGuid); }
		}

		public ToolWindowContent? GetOrCreate(Guid guid) {
			if (guid != StepperConstants.ToolWindowGuid)
				return null;
			return content ??= new StepperToolWindowContent(service.ILAstLanguages.First(a => a.View == UpstreamILAstView.Transformed),
				documentTabService, treeViewService, treeViewNodeTextElementProvider);
		}
	}

	/// <summary>
	/// Shows the stepper when the ILAst view is selected, and tells the ILAst view not to cache the output it records
	/// steps for (the stepper shows the steps of the method that was decompiled last)
	/// </summary>
	[ExportAutoLoaded]
	sealed class StepperLoader : IAutoLoaded {
		readonly IDecompilerService decompilerService;
		readonly IDsToolWindowService toolWindowService;

		[ImportingConstructor]
		StepperLoader(UpstreamLanguageService service, IDecompilerService decompilerService, IDsToolWindowService toolWindowService) {
			this.decompilerService = decompilerService;
			this.toolWindowService = toolWindowService;
			foreach (var language in service.ILAstLanguages)
				language.DisableOutputCaching = output => (output as IDocumentViewerOutput)?.DisableCaching();
			decompilerService.DecompilerChanged += (s, e) => ShowIfSelected();
			ShowIfSelected();
		}

		void ShowIfSelected() {
			if (decompilerService.Decompiler is UpstreamILAstLanguage { View: UpstreamILAstView.Transformed })
				toolWindowService.Show(StepperConstants.ToolWindowGuid);
		}
	}

	interface IStepperTreeNodeDataContext {
		ITreeView TreeView { get; }
		ITreeViewNodeTextElementProvider TreeViewNodeTextElementProvider { get; }
		Stepper.Node? CurrentState { get; }
		void ShowStateAfter(Stepper.Node node);
	}

	sealed class StepperToolWindowContent : ToolWindowContent, IFocusable, IStepperTreeNodeDataContext {
		readonly UpstreamILAstLanguage decompiler;
		readonly IDocumentTabService documentTabService;
		readonly ITreeView treeView;

		public override object UIObject => treeView.UIObject;
		public override IInputElement? FocusedElement => null;
		public override FrameworkElement ZoomElement => treeView.UIObject;
		public override Guid Guid => StepperConstants.ToolWindowGuid;
		public override string Title => "ILAst Stepper";
		public bool CanFocus => true;
		public ITreeView TreeView => treeView;
		public ITreeViewNodeTextElementProvider TreeViewNodeTextElementProvider { get; }
		public Stepper.Node? CurrentState { get; private set; }

		public StepperToolWindowContent(UpstreamILAstLanguage decompiler, IDocumentTabService documentTabService, ITreeViewService treeViewService, ITreeViewNodeTextElementProvider treeViewNodeTextElementProvider) {
			this.decompiler = decompiler;
			this.documentTabService = documentTabService;
			TreeViewNodeTextElementProvider = treeViewNodeTextElementProvider;
			var options = new TreeViewOptions {
				CanDragAndDrop = false,
				SelectionMode = SelectionMode.Single,
			};
			treeView = treeViewService.Create(StepperConstants.TreeViewGuid, options);
			treeView.UIObject.Padding = new Thickness(0, 2, 0, 2);
			treeView.UIObject.BorderThickness = new Thickness(1);
			decompiler.StepperUpdated += Decompiler_StepperUpdated;
			ShowSteps();
		}

		// Raised on the decompiler's thread
		void Decompiler_StepperUpdated(object? sender, EventArgs e) =>
			treeView.UIObject.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ShowSteps));

		void ShowSteps() {
			CurrentState = null;
			treeView.Root.Children.Clear();
			var method = decompiler.StepperMethod;
			if (method is null)
				return;
			treeView.Root.AddChild(treeView.Create(new StepperTextTreeNodeData(this, method.FullName)));
			foreach (var step in decompiler.Stepper.Steps)
				treeView.Root.AddChild(treeView.Create(new StepperNodeTreeNodeData(this, step)));
		}

		public void ShowStateAfter(Stepper.Node node) {
			CurrentState = node;
			decompiler.SetStepLimit(node.EndStep);
			RefreshTabs();
			treeView.RefreshAllNodes();
		}

		void RefreshTabs() {
			var tabs = documentTabService.VisibleFirstTabs.Where(tab => (tab.Content as IDecompilerTabContent)?.Decompiler == decompiler).ToArray();
			if (tabs.Length != 0)
				documentTabService.Refresh(tabs);
		}

		public void Focus() => treeView.Focus();
	}

	abstract class StepperTreeNodeDataBase : TreeNodeData {
		protected readonly IStepperTreeNodeDataContext context;

		static readonly TextClassifierTextColorWriter writer = new TextClassifierTextColorWriter();

		protected StepperTreeNodeDataBase(IStepperTreeNodeDataContext context) => this.context = context;

		public override Guid Guid => Guid.Empty;
		public override object? ToolTip => null;
		public override ImageReference Icon => ImageReference.None;
		public override void OnRefreshUI() { }

		protected abstract void Write(TextClassifierTextColorWriter writer);

		public override object Text {
			get {
				try {
					Write(writer);
					var classifierContext = new TreeViewNodeClassifierContext(writer.Text, context.TreeView, this, isToolTip: false, colorize: true, colors: writer.Colors);
					return context.TreeViewNodeTextElementProvider.CreateTextElement(classifierContext, TreeViewContentTypes.TreeViewNode, TextElementFlags.FilterOutNewLines);
				}
				finally {
					writer.Clear();
				}
			}
		}
	}

	/// <summary>
	/// The method the steps belong to
	/// </summary>
	sealed class StepperTextTreeNodeData : StepperTreeNodeDataBase {
		readonly string text;

		public StepperTextTreeNodeData(IStepperTreeNodeDataContext context, string text) : base(context) => this.text = text;

		protected override void Write(TextClassifierTextColorWriter writer) => writer.Write(BoxedTextColor.Comment, text);
	}

	/// <summary>
	/// A step: activating it shows the ILAst after the step
	/// </summary>
	sealed class StepperNodeTreeNodeData : StepperTreeNodeDataBase {
		readonly Stepper.Node node;

		public StepperNodeTreeNodeData(IStepperTreeNodeDataContext context, Stepper.Node node) : base(context) => this.node = node;

		protected override void Write(TextClassifierTextColorWriter writer) =>
			writer.Write(node == context.CurrentState ? BoxedTextColor.Blue : BoxedTextColor.Text, node.Description);

		public override IEnumerable<TreeNodeData> CreateChildren() {
			foreach (var child in node.Children)
				yield return new StepperNodeTreeNodeData(context, child);
		}

		public override bool Activate() {
			context.ShowStateAfter(node);
			return true;
		}
	}
}
#endif
