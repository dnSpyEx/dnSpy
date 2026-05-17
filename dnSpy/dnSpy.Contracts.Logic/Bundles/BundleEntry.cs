using System.IO;
using dnlib.DotNet;
using dnlib.PE;

namespace dnSpy.Contracts.Bundles {
	/// <summary>
	/// Represents one entry in a <see cref="SingleFileBundle"/>
	/// </summary>
	public abstract class BundleEntry {
		/// <summary>
		/// The type of the entry
		/// </summary>
		/// <seealso cref="BundleEntryType"/>
		public abstract BundleEntryType Type { get; }

		/// <summary>
		/// Path of an embedded file, relative to the Bundle source-directory.
		/// </summary>
		public string RelativePath { get; set; }

		/// <summary>
		/// The file name of the entry.
		/// </summary>
		public string FileName {
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
				parentFolder?.Entries.Remove(this);
				value?.Entries.Add(this);
			}
		}
		internal BundleFolder? parentFolder;

		/// <summary>
		/// Indicates if the entry is compressed
		/// </summary>
		public bool IsCompressed { get; set; }

		/// <summary>
		///
		/// </summary>
		/// <param name="relativePath"></param>
		protected BundleEntry(string relativePath) => RelativePath = relativePath;
	}

	/// <summary>
	///
	/// </summary>
	public abstract class UnknownBundleEntry : BundleEntry {
		/// <inheritdoc/>
		public override BundleEntryType Type => BundleEntryType.Unknown;

		/// <summary>
		///
		/// </summary>
		public abstract byte[] Data { get; }

		/// <inheritdoc/>
		protected UnknownBundleEntry(string relativePath) : base(relativePath) { }
	}

	/// <summary>
	///
	/// </summary>
	public abstract class AssemblyBundleEntry : BundleEntry {
		/// <inheritdoc/>
		public override BundleEntryType Type => BundleEntryType.Assembly;

		/// <summary>
		///
		/// </summary>
		public abstract ModuleDefMD Module { get; }

		/// <inheritdoc/>
		protected AssemblyBundleEntry(string relativePath) : base(relativePath) { }
	}

	/// <summary>
	///
	/// </summary>
	public abstract class NativeBinaryBundleEntry : BundleEntry {
		/// <inheritdoc/>
		public override BundleEntryType Type => BundleEntryType.NativeBinary;

		/// <summary>
		///
		/// </summary>
		public abstract PEImage PEImage { get; }

		/// <inheritdoc/>
		protected NativeBinaryBundleEntry(string relativePath) : base(relativePath) { }
	}

	/// <summary>
	///
	/// </summary>
	public abstract class ConfigJSONBundleEntry : BundleEntry {
		/// <inheritdoc/>
		public override BundleEntryType Type { get; }

		/// <summary>
		///
		/// </summary>
		public abstract string JsonText { get; }

		/// <inheritdoc/>
		protected ConfigJSONBundleEntry(BundleEntryType type, string relativePath) : base(relativePath) => Type = type;
	}

	/// <summary>
	///
	/// </summary>
	public abstract class SymbolBundleEntry : BundleEntry {
		/// <inheritdoc/>
		public override BundleEntryType Type => BundleEntryType.Symbols;

		/// <summary>
		///
		/// </summary>
		public abstract byte[] Data { get; }

		/// <inheritdoc/>
		protected SymbolBundleEntry(string relativePath) : base(relativePath) { }
	}
}
