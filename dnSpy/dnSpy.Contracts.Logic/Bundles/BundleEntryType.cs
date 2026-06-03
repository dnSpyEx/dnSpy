namespace dnSpy.Contracts.Bundles {
	/// <summary>
	/// BundleEntryType: Identifies the type of file embedded into the bundle.
	///
	/// The bundler differentiates a few kinds of files via the manifest,
	/// with respect to the way in which they'll be used by the runtime.
	/// </summary>
	public enum BundleEntryType : byte {
		/// <summary>
		/// Type not determined.
		/// </summary>
		Unknown,

		/// <summary>
		/// IL and R2R Assemblies
		/// </summary>
		Assembly,

		/// <summary>
		/// Native Binaries
		/// </summary>
		NativeBinary,

		/// <summary>
		/// .deps.json configuration file
		/// </summary>
		DepsJson,

		/// <summary>
		/// .runtimeconfig.json configuration file
		/// </summary>
		RuntimeConfigJson,

		/// <summary>
		/// PDB Files
		/// </summary>
		Symbols
	}
}
