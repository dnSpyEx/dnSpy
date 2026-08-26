using System.Text;

namespace dnSpy.Contracts.Bundles {
	/// <summary>
	///
	/// </summary>
	public static class BundleExtensions {
		/// <summary>
		///
		/// </summary>
		/// <param name="entry"></param>
		/// <returns></returns>
		public static byte[]? GetEntryData(this BundleEntry entry) {
			switch (entry) {
			case AssemblyBundleEntryMD assemblyEntry:
				return assemblyEntry.Data;
			case ConfigJSONBundleEntry configEntry:
				return Encoding.UTF8.GetBytes(configEntry.JsonText);
			case NativeBinaryBundleEntryMD nativeEntry:
				return nativeEntry.Data;
			case SymbolBundleEntry symbolEntry:
				return symbolEntry.Data;
			case UnknownBundleEntry unknownEntry:
				return unknownEntry.Data;
			default:
				return null;
			}
		}
	}
}
