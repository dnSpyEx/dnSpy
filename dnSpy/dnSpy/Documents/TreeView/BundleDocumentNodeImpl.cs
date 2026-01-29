using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnSpy.Contracts.Bundles;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.TreeView;
using dnSpy.Decompiler;

namespace dnSpy.Documents.TreeView {
	sealed class BundleDocumentNodeImpl : BundleDocumentNode {
		public BundleDocumentNodeImpl(IDsDocument document) : base(document) { }

		public override Guid Guid => new Guid(DocumentTreeViewConstants.BUNDLE_NODE_GUID);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => dnImgMgr.GetImageReference(Document.PEImage!);
		public override void Initialize() => TreeNode.LazyLoading = true;

		public override IEnumerable<TreeNodeData> CreateChildren() {
			Debug2.Assert(Document.SingleFileBundle is not null);

			var children = Document.Children;

			foreach (var bundleFolder in Document.SingleFileBundle.TopLevelFolders)
				yield return new BundleFolderNodeImpl(this, bundleFolder);

			var documentMap = new Dictionary<BundleEntry, IDsDocument>();
			foreach (var childDocument in children) {
				if (childDocument.BundleEntry is not null && childDocument.BundleEntry.ParentFolder is null)
					documentMap[childDocument.BundleEntry] = childDocument;
			}

			foreach (var entry in Document.SingleFileBundle.TopLevelEntries) {
				if (documentMap.TryGetValue(entry, out var document))
					yield return Context.DocumentTreeView.CreateNode(this, document);
				else {
					switch (entry.Type) {
					case BundleEntryType.Unknown:
					case BundleEntryType.Symbols:
						yield return new UnknownBundleEntryNodeImpl(entry);
						break;
					case BundleEntryType.DepsJson:
					case BundleEntryType.RuntimeConfigJson:
						yield return new JsonBundleEntryNodeImpl(entry);
						break;
					default:
						throw new ArgumentOutOfRangeException();
					}
				}
			}

			// TODO: return all bundle entries
		}

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			Debug2.Assert(Document.SingleFileBundle is not null);
			Debug2.Assert(Document.PEImage is not null);
			if ((options & DocumentNodeWriteOptions.ToolTip) == 0)
				new NodeFormatter().Write(output, decompiler, Document);
			else {
				output.Write(BoxedTextColor.Text, TargetFrameworkUtils.GetArchString(Document.PEImage.ImageNTHeaders.FileHeader.Machine));

				output.WriteLine();
				output.WriteFilename(Document.Filename);
			}
		}

		public override FilterType GetFilterType(IDocumentTreeNodeFilter filter) =>
			filter.GetResult(Document).FilterType;
	}
}
