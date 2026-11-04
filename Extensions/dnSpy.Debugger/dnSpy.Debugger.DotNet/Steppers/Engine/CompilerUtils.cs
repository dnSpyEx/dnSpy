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

using System;
using System.Diagnostics;
using dnlib.DotNet;
using dnlib.DotNet.Emit;
using dnSpy.Contracts.Debugger;
using dnSpy.Contracts.Debugger.DotNet.Code;
using dnSpy.Contracts.Decompiler;
using dnSpy.Debugger.DotNet.Metadata;

namespace dnSpy.Debugger.DotNet.Steppers.Engine {
	static class CompilerUtils {
		public static bool IsIgnoredIteratorStateMachineMethod(MethodDef method) {
			var declType = method.DeclaringType;
			if (declType.DeclaringType is null)
				return false;
			if (!declType.IsDefined(utf8System_Runtime_CompilerServices, utf8CompilerGeneratedAttribute))
				return false;

			bool result = false;
			foreach (var ii in declType.Interfaces) {
				if (ii.Interface.FullName == "System.Collections.IEnumerator") {
					result = true;
					break;
				}
			}
			if (!result)
				return false;

			if (method.IsVirtual) {
				// Ignore everything except MoveNext
				if (method.Name == utf8MoveNext)
					return false;
				foreach (var ovr in method.Overrides) {
					if (ovr.MethodDeclaration.Name == utf8MoveNext)
						return false;
				}
			}
			return true;
		}
		static readonly UTF8String utf8MoveNext = new UTF8String("MoveNext");
		static readonly UTF8String utf8System_Runtime_CompilerServices = new UTF8String("System.Runtime.CompilerServices");
		static readonly UTF8String utf8CompilerGeneratedAttribute = new UTF8String("CompilerGeneratedAttribute");

		// The C# compiler's entry point for an async Main, 'static void <Main>(string[] args) { Main(args).GetAwaiter().GetResult(); }':
		// a cheap check of its name, the stepper calls it for every step over
		public static bool MaybeAsyncMainEntryPoint(DbgModule module, uint token) {
			var method = module.GetReflectionModule()?.ResolveMethod((int)token, DmdResolveOptions.None);
			return method is not null && method.IsStatic && method.Name == "<Main>";
		}

		// The C# compiler's entry point for an async Main (see above): gets the IL offset of its call of Main
		public static bool TryGetAsyncMainCallOffset(MethodDef method, out uint callOffset) {
			callOffset = 0;
			// (Like the decompiler's check: the module's entry point named <Main>)
			if (!method.IsStatic || method.Name != "<Main>" || method.Module?.EntryPoint != method || method.Body is not CilBody body)
				return false;
			foreach (var instr in body.Instructions) {
				if (instr.OpCode == OpCodes.Call && instr.Operand is MethodDef main && main.DeclaringType == method.DeclaringType) {
					callOffset = instr.Offset;
					return true;
				}
			}
			return false;
		}

		// Stepping over the compiler's entry point of an async Main steps into Main if it's before the call of Main and the
		// method's first statement includes the call and the code after it (that waits for Main): the decompiled code shows
		// it as Main's declaration (with the compiler's PDB it has no source at all), and stepping over it would run the
		// whole program. Not when the statement is only the call or doesn't include it (eg. IL: each instruction is a
		// statement, and a Main without parameters is called by the first instruction).
		public static bool IsBeforeAsyncMainCall(DbgMethodDebugInfo debugInfo, uint offset) {
			if (!TryGetAsyncMainCallOffset(debugInfo.Method, out var callOffset))
				return false;
			// (After the call: Main returned, it's a normal step over)
			if (offset != DbgDotNetInstructionOffsetConstants.PROLOG && offset > callOffset)
				return false;
			var instructions = debugInfo.Method.Body.Instructions;
			int callIndex = -1;
			for (int i = 0; i < instructions.Count; i++) {
				if (instructions[i].Offset == callOffset) {
					callIndex = i;
					break;
				}
			}
			if (callIndex < 0 || callIndex + 1 >= instructions.Count)
				return false;
			var first = debugInfo.GetSourceStatementByCodeOffset(0);
			var call = debugInfo.GetSourceStatementByCodeOffset(callOffset);
			var afterCall = debugInfo.GetSourceStatementByCodeOffset(instructions[callIndex + 1].Offset);
			return first is not null && call is not null && afterCall is not null &&
				first.Value.TextSpan == call.Value.TextSpan && call.Value.TextSpan == afterCall.Value.TextSpan;
		}

		// The compiler adds a new instance method to the class if a base member is accessed using 'base.XXX'
		// from inside an iterator or async method. That method just calls the real base method.
		public static bool IsBaseWrapperMethod(DbgModule module, uint token) {
			var reflectionModule = module.GetReflectionModule();
			Debug2.Assert(reflectionModule is not null);
			if (reflectionModule is null)
				return false;
			var method = reflectionModule.ResolveMethod((int)token, DmdResolveOptions.None);
			Debug2.Assert(method is not null);
			if (method is null)
				return false;

			if (!method.IsPrivate || method.IsStatic || method.IsAbstract || method.IsVirtual)
				return false;
			var name = method.Name;
			if (string.IsNullOrEmpty(name))
				return false;

			bool okName = false;
			var c = name[0];
			if (c == '<') {
				// Roslyn C#, eg. "<>n__2"
				if (name.StartsWith("<>n__", StringComparison.Ordinal))
					okName = true;
				// mcs, eg. "<GetString>__BaseCallProxy1"
				else if (name.IndexOf(">__BaseCallProxy", StringComparison.Ordinal) >= 0)
					okName = true;
			}
			// VB, eg. "$VB$ClosureStub_GetString_MyBase"
			else if (c == '$' && name.StartsWith("$VB$ClosureStub_", StringComparison.Ordinal) && name.EndsWith("_MyBase", StringComparison.Ordinal))
				okName = true;

			if (!okName)
				return false;

			return method.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute", inherit: false);
		}
	}
}
