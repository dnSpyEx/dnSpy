using System.IO;
using System.IO.Compression;
using dnlib.IO;

namespace dnSpy.Contracts.Bundles {
	static class BundleEntryMDUtils {
		internal static byte[] ReadBundleData(DataReaderFactory dataReaderFactory, uint offset, uint size, bool isCompressed, uint decompressedSize) {
			var reader = dataReaderFactory.CreateReader(offset, size);
			if (!isCompressed)
				return reader.ReadRemainingBytes();

			using (var decompressedStream = new MemoryStream((int)decompressedSize)) {
				using (var compressedStream = reader.AsStream()) {
					using (var deflateStream = new DeflateStream(compressedStream, CompressionMode.Decompress)) {
						deflateStream.CopyTo(decompressedStream);
					}
				}
				return decompressedStream.ToArray();
			}
		}
	}
}
