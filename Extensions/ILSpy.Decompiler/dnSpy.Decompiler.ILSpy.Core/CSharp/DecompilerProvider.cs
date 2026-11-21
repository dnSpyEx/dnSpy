using System;
using System.Collections.Generic;
using System.Diagnostics;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.CSharp.Legacy;
using dnSpy.Decompiler.ILSpy.Core.Settings;

namespace dnSpy.Decompiler.ILSpy.Core.CSharp {
	sealed class DecompilerProvider : IDecompilerProvider {
		readonly DecompilerSettingsService decompilerSettingsService;

		// Keep the default ctor. It's used by dnSpy.Console.exe
		public DecompilerProvider()
			: this(DecompilerSettingsService.__Instance_DONT_USE) {
		}

		public DecompilerProvider(DecompilerSettingsService decompilerSettingsService) {
			Debug2.Assert(decompilerSettingsService is not null);
			this.decompilerSettingsService = decompilerSettingsService ?? throw new ArgumentNullException(nameof(decompilerSettingsService));
		}

		public IEnumerable<IDecompiler> Create() {
			yield return new CSharpDecompiler(decompilerSettingsService.CSharpVBDecompilerSettings, DecompilerConstants.CSHARP_ILSPY_ORDERUI);
#if DEBUG
			foreach (var l in CSharpDecompiler.GetDebugDecompilers(decompilerSettingsService.CSharpVBDecompilerSettings))
				yield return l;
#endif
			yield return new LegacyCSharpDecompiler(decompilerSettingsService.CSharpVBDecompilerSettings, DecompilerConstants.CSHARP_ILSPY_LEGACY_ORDERUI);
#if DEBUG
			foreach (var l in LegacyCSharpDecompiler.GetDebugDecompilers(decompilerSettingsService.CSharpVBDecompilerSettings))
				yield return l;
#endif
		}
	}
}
