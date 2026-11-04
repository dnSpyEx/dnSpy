/*
    Copyright (C) 2026 dnSpy contributors

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using dnlib.DotNet;
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	static class TestDecompiler {
		public static DecompilerSettings CreateSettings() =>
			new DecompilerSettings(LanguageVersion.Latest) {
				ThrowOnAssemblyResolveErrors = false,
			};

		public static UpstreamCSharpDecompiler Create(Func<ModuleDef, bool>? isModified = null, bool alwaysNormalize = false) =>
			new UpstreamCSharpDecompiler(new ModuleBridgeCache(new ModuleBridgeOptions { AlwaysNormalize = alwaysNormalize }, isModified), CreateSettings());
	}
}
