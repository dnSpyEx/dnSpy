// Copyright (c) 2011 AlphaSierraPapa for the SharpDevelop Team
//
// Permission is hereby granted, free of charge, to any person obtaining a copy of this
// software and associated documentation files (the "Software"), to deal in the Software
// without restriction, including without limitation the rights to use, copy, modify, merge,
// publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons
// to whom the Software is furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all copies or
// substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED,
// INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR
// PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE
// FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR
// OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER
// DEALINGS IN THE SOFTWARE.

using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Decompiler.ILSpy.Core.Disassembler {
	/// <summary>
	/// The helpers the IL disassembler used from the old decompiler (DnlibExtensions, OpCodeInfo)
	/// </summary>
	static class DisassemblerUtils {
		public static string OffsetToString(uint offset) => string.Format("IL_{0:X4}", offset);

		public static bool IsValueType(ITypeDefOrRef? tdr) {
			if (tdr is null)
				return false;
			if (tdr is TypeSpec ts)
				return IsValueType(ts.TypeSig);
			return tdr.IsValueType;
		}

		public static bool IsValueType(TypeSig? ts) => ts?.IsValueType ?? false;

		public static bool IsUnconditionalBranch(OpCode opcode) {
			if (opcode.OpCodeType == OpCodeType.Prefix || opcode.OpCodeType == OpCodeType.Nternal)
				return false;
			switch (opcode.FlowControl) {
			case FlowControl.Branch:
			case FlowControl.Throw:
			case FlowControl.Return:
				return true;
			default:
				return false;
			}
		}
	}
}
