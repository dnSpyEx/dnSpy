using System.Diagnostics;
using dnSpy.Contracts.Bundles;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// JSON bundle entry node
	/// </summary>
	public abstract class JsonBundleEntryNode : DocumentTreeNodeData, IBundleEntryNode {
		/// <inheritdoc/>
		public BundleEntry BundleEntry { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		protected JsonBundleEntryNode(BundleEntry bundleEntry) {
			Debug2.Assert(bundleEntry is not null);
			BundleEntry = bundleEntry;
		}
	}
}
