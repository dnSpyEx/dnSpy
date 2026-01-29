using System;
using System.Text;
using dnSpy.Contracts.Bundles;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.Tabs.DocViewer;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;

namespace dnSpy.Documents.TreeView {
	public class JsonBundleEntryNodeImpl : JsonBundleEntryNode, IDecompileSelf {
		readonly BundleEntry bundleEntry;

		public JsonBundleEntryNodeImpl(BundleEntry bundleEntry) : base(bundleEntry) => this.bundleEntry = bundleEntry;

		public override Guid Guid => new Guid(DocumentTreeViewConstants.BUNDLE_JSON_ENTRY_NODE_GUID);

		public override NodePathName NodePathName => new NodePathName(Guid);

		protected override ImageReference GetIcon(IDotNetImageService dnImgMgr) => DsImages.TextFile;

		protected override void WriteCore(ITextColorWriter output, IDecompiler decompiler, DocumentNodeWriteOptions options) {
			// TODO: better tooltip
			output.Write(BoxedTextColor.Text, bundleEntry.FileName);
		}

		public bool Decompile(IDecompileNodeContext context) {
			//TODO: implement syntax highlighting
			context.Output.Write(((ConfigJSONBundleEntry)bundleEntry).JsonText, BoxedTextColor.Text);
			return true;
		}
	}
}
