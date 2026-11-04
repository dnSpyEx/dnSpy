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

using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	static class TestSetup {
		/// <summary>
		/// In production a bug in the statement shaper only loses its fixes. In the tests it fails the test.
		/// </summary>
		[ModuleInitializer]
		internal static void Initialize() => StatementShaper.OnError = (method, ex) => ExceptionDispatchInfo.Throw(ex);
	}
}
