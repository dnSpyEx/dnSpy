using System.Diagnostics;
using dnSpy.Contracts.Bundles;

namespace dnSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// Bundle folder node
	/// </summary>
	public abstract class BundleFolderNode : DocumentTreeNodeData {
		/// <summary>
		/// Constructor
		/// </summary>
		protected BundleFolderNode(BundleFolder bundleFolder) => Debug2.Assert(bundleFolder is not null);
	}
}
