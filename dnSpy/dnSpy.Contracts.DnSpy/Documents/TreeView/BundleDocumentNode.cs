using System.Diagnostics;

namespace dnSpy.Contracts.Documents.TreeView {
	/// <summary>
	/// A .NEt single file bundle.
	/// </summary>
	public abstract class BundleDocumentNode : DsDocumentNode {
		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="document">Document</param>
		protected BundleDocumentNode(IDsDocument document) : base(document) => Debug2.Assert(document.SingleFileBundle is not null);
	}
}
