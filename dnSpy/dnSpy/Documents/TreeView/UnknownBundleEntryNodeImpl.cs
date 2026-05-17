using System;
using dnSpy.Contracts.Bundles;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;

namespace dnSpy.Documents.TreeView {
	sealed class UnknownBundleEntryNodeImpl : UnknownBundleEntryNode {
		readonly BundleEntry bundleEntry;

		public UnknownBundleEntryNodeImpl(BundleEntry bundleEntry) : base(bundleEntry) {
			this.bundleEntry = bundleEntry;
		}

		public override Guid Guid => new Guid(DocumentTreeViewConstants.BUNDLE_UNKNOWN_ENTRY_NODE_GUID);
		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.BinaryFile;
		public override NodePathName NodePathName => new NodePathName(Guid);

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			// TODO: better tooltip
			output.Write(BoxedTextColor.Text, bundleEntry.FileName);
		}
	}
}
