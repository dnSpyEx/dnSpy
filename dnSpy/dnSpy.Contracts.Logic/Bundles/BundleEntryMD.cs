using System.Text;
using System.Threading;
using dnlib.DotNet;
using dnlib.IO;
using dnlib.PE;

namespace dnSpy.Contracts.Bundles {
	sealed class UnknownBundleEntryMD : UnknownBundleEntry {
		readonly DataReaderFactory dataReaderFactory;
		readonly uint offset;
		readonly uint size;
		readonly bool isDataCompressed;
		readonly uint decompressedSize;

		public override byte[] Data {
			get {
				if (data is null)
					Interlocked.CompareExchange(ref data, ReadData(), null);
				return data;
			}
		}
		byte[]? data;

		byte[] ReadData() => BundleEntryMDUtils.ReadBundleData(dataReaderFactory, offset, size, isDataCompressed, decompressedSize);

		public UnknownBundleEntryMD(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize, string relativePath) : base(relativePath) {
			this.dataReaderFactory = dataReaderFactory;
			this.offset = offset;
			this.size = size;
			IsCompressed = isDataCompressed = isCompressed;
			this.decompressedSize = decompressedSize;
		}
	}

	sealed class AssemblyBundleEntryMD : AssemblyBundleEntry {
		readonly DataReaderFactory dataReaderFactory;
		readonly uint offset;
		readonly uint size;
		readonly bool isDataCompressed;
		readonly uint decompressedSize;
		readonly ModuleCreationOptions modCreationOptions;

		public override ModuleDefMD Module {
			get {
				if (module is null)
					Interlocked.CompareExchange(ref module, InitializeModule(), null);
				return module;
			}
		}
		ModuleDefMD? module;

		ModuleDefMD InitializeModule() => ModuleDefMD.Load(Data, modCreationOptions);

		internal byte[] Data {
			get {
				if (data is null)
					Interlocked.CompareExchange(ref data, ReadData(), null);
				return data;
			}
		}
		byte[]? data;

		byte[] ReadData() => BundleEntryMDUtils.ReadBundleData(dataReaderFactory, offset, size, isDataCompressed, decompressedSize);

		public AssemblyBundleEntryMD(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize, ModuleCreationOptions modCreationOptions, string relativePath) : base(relativePath) {
			this.dataReaderFactory = dataReaderFactory;
			this.offset = offset;
			this.size = size;
			IsCompressed = isDataCompressed = isCompressed;
			this.decompressedSize = decompressedSize;
			this.modCreationOptions = modCreationOptions;
		}
	}

	sealed class NativeBinaryBundleEntryMD : NativeBinaryBundleEntry {
		readonly DataReaderFactory dataReaderFactory;
		readonly uint offset;
		readonly uint size;
		readonly bool isDataCompressed;
		readonly uint decompressedSize;

		public override PEImage PEImage {
			get {
				if (peImage is null)
					Interlocked.CompareExchange(ref peImage, InitializePEImage(), null);
				return peImage;
			}
		}
		PEImage? peImage;

		PEImage InitializePEImage() => new PEImage(Data);

		internal byte[] Data {
			get {
				if (data is null)
					Interlocked.CompareExchange(ref data, ReadData(), null);
				return data;
			}
		}
		byte[]? data;

		byte[] ReadData() => BundleEntryMDUtils.ReadBundleData(dataReaderFactory, offset, size, isDataCompressed, decompressedSize);

		public NativeBinaryBundleEntryMD(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize, string relativePath) : base(relativePath) {
			this.dataReaderFactory = dataReaderFactory;
			this.offset = offset;
			this.size = size;
			IsCompressed = isDataCompressed = isCompressed;
			this.decompressedSize = decompressedSize;
		}
	}

	sealed class ConfigJSONBundleEntryMD : ConfigJSONBundleEntry {
		readonly DataReaderFactory dataReaderFactory;
		readonly uint offset;
		readonly uint size;
		readonly bool isDataCompressed;
		readonly uint decompressedSize;

		public override string JsonText {
			get {
				if (jsonText is null)
					Interlocked.CompareExchange(ref jsonText, ReadJSONText(), null);
				return jsonText;
			}
		}
		string? jsonText;

		string ReadJSONText() => Encoding.UTF8.GetString(BundleEntryMDUtils.ReadBundleData(dataReaderFactory, offset, size, isDataCompressed, decompressedSize));

		public ConfigJSONBundleEntryMD(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize, BundleEntryType type, string relativePath) : base(type, relativePath) {
			this.dataReaderFactory = dataReaderFactory;
			this.offset = offset;
			this.size = size;
			IsCompressed = isDataCompressed = isCompressed;
			this.decompressedSize = decompressedSize;
		}
	}

	sealed class SymbolBundleEntryMD : SymbolBundleEntry {
		readonly DataReaderFactory dataReaderFactory;
		readonly uint offset;
		readonly uint size;
		readonly bool isDataCompressed;
		readonly uint decompressedSize;

		public override byte[] Data {
			get {
				if (data is null)
					Interlocked.CompareExchange(ref data, ReadData(), null);
				return data;
			}
		}
		byte[]? data;

		byte[] ReadData() => BundleEntryMDUtils.ReadBundleData(dataReaderFactory, offset, size, isDataCompressed, decompressedSize);

		public SymbolBundleEntryMD(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize, string relativePath) : base(relativePath) {
			this.dataReaderFactory = dataReaderFactory;
			this.offset = offset;
			this.size = size;
			IsCompressed = isDataCompressed = isCompressed;
			this.decompressedSize = decompressedSize;
		}
	}
}
