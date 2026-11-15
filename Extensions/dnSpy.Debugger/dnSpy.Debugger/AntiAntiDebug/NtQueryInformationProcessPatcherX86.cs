/*
    Copyright (C) 2014-2019 de4dot@gmail.com

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

using System.Diagnostics.CodeAnalysis;
using dnSpy.Contracts.Debugger.AntiAntiDebug;
using Iced.Intel;

namespace dnSpy.Debugger.AntiAntiDebug {
	sealed class NtQueryInformationProcessPatcherX86 : PatcherX86 {
		public NtQueryInformationProcessPatcherX86(DbgNativeFunctionHookContext context) : base(context) { }

		public bool TryPatchX86(string dllName, [NotNullWhen(false)] out string? errorMessage) {
			var function = functionProvider.GetFunction(dllName, NtQueryInformationProcessHook.FuncName);

			var c = new Assembler(process.Bitness);
			NtQueryInformationProcessAssembler.AssembleX86(c, function.NewFunctionAddress);

			if (!c.TryAssemble(new CodeWriterImpl(function), function.NewCodeAddress, out var encErrMsg, out _)) {
				errorMessage = $"Failed to encode: {encErrMsg}";
				return false;
			}

			errorMessage = null;
			return true;
		}

		public bool TryPatchX64(string dllName, [NotNullWhen(false)] out string? errorMessage) {
			var function = functionProvider.GetFunction(dllName, NtQueryInformationProcessHook.FuncName);

			var c = new Assembler(process.Bitness);
			NtQueryInformationProcessAssembler.AssembleX64(c, function.NewFunctionAddress);

			if (!c.TryAssemble(new CodeWriterImpl(function), function.NewCodeAddress, out var encErrMsg, out _)) {
				errorMessage = $"Failed to encode: {encErrMsg}";
				return false;
			}

			errorMessage = null;
			return true;
		}
	}
}
