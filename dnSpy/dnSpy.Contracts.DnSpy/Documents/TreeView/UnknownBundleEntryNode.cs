using System.Diagnostics;
using dnSpy.Contracts.Bundles;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// Unknown bundle entry node
	/// </summary>
	public abstract class UnknownBundleEntryNode : DocumentTreeNodeData, IBundleEntryNode  {
		/// <inheritdoc/>
		public BundleEntry BundleEntry { get; }

		/// <summary>
		/// Constructor
		/// </summary>
		protected UnknownBundleEntryNode(BundleEntry bundleEntry) {
			Debug2.Assert(bundleEntry is not null);
			BundleEntry = bundleEntry;
		}
	}
}
