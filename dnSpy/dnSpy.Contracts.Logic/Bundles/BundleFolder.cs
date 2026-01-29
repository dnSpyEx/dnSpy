using System.Collections.Generic;
using System.IO;
using System.Threading;
using dnlib.Utils;

namespace dnSpy.Contracts.Bundles {
	/// <summary>
	/// Represents one folder in a <see cref="SingleFileBundle"/>
	/// </summary>
	public sealed class BundleFolder : IListListener<BundleEntry>, IListListener<BundleFolder> {
		/// <summary>
		/// Gets the relative path of the folder.
		/// </summary>
		public string RelativePath { get; set; }

		/// <summary>
		/// Gets the short name of the folder.
		/// </summary>
		public string Name {
			get => Path.GetFileName(RelativePath);
			set => RelativePath = Path.Combine(Path.GetDirectoryName(RelativePath) ?? string.Empty, value);
		}

		/// <summary>
		/// The parent folder
		/// </summary>
		public BundleFolder? ParentFolder {
			get => parentFolder;
			set {
				if (parentFolder == value)
					return;
				parentFolder?.NestedFolders.Remove(this);
				value?.NestedFolders.Add(this);
			}
		}
		internal BundleFolder? parentFolder;

		/// <summary>
		/// The folders nested within this folder.
		/// </summary>
		public IList<BundleFolder> NestedFolders {
			get {
				if (nestedFolders is null)
					Interlocked.CompareExchange(ref nestedFolders, new LazyList<BundleFolder>(this), null);
				return nestedFolders;
			}
		}
		LazyList<BundleFolder>? nestedFolders;

		/// <summary>
		/// The entries in this folder.
		/// </summary>
		public IList<BundleEntry> Entries {
			get {
				if (entries is null)
					Interlocked.CompareExchange(ref entries, new LazyList<BundleEntry>(this), null);
				return entries;
			}
		}
		LazyList<BundleEntry>? entries;

		/// <summary>
		/// Creates a folder with the provided relative path.
		/// </summary>
		public BundleFolder(string relativePath) => RelativePath = relativePath;

		void IListListener<BundleEntry>.OnLazyAdd(int index, ref BundleEntry value) { }
		void IListListener<BundleEntry>.OnAdd(int index, BundleEntry value) => value.parentFolder = this;
		void IListListener<BundleEntry>.OnRemove(int index, BundleEntry value) => value.parentFolder = null;
		void IListListener<BundleEntry>.OnResize(int index) { }
		void IListListener<BundleEntry>.OnClear() {
			foreach (var entry in entries!)
				entry.parentFolder = null;
		}

		void IListListener<BundleFolder>.OnLazyAdd(int index, ref BundleFolder value) { }
		void IListListener<BundleFolder>.OnAdd(int index, BundleFolder value) => value.parentFolder = this;
		void IListListener<BundleFolder>.OnRemove(int index, BundleFolder value) => value.parentFolder = null;
		void IListListener<BundleFolder>.OnResize(int index) { }
		void IListListener<BundleFolder>.OnClear() {
			foreach (var folder in nestedFolders!)
				folder.parentFolder = null;
		}
	}
}
