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

using Iced.Intel;
using static Iced.Intel.AssemblerRegisters;

namespace dnSpy.Debugger.AntiAntiDebug {
	static class NtQueryInformationProcessAssembler {
		public const int ProcessDebugPort = 7;
		public const int ProcessDebugObjectHandle = 30;
		public const int ProcessDebugFlags = 31;
		public const uint STATUS_PORT_NOT_SET = 0xC0000353;

		public static void AssembleX86(Assembler c, ulong originalFunctionAddress) {
			var done = c.CreateLabel();
			var debugPort = c.CreateLabel();
			var debugObject = c.CreateLabel();
			var portNotSet = c.CreateLabel();
			var debugFlags = c.CreateLabel();

			// NtQueryInformationProcess(handle, class, info, length, returnLength)
			c.push(ebp);
			c.mov(ebp, esp);
			c.push(__dword_ptr[ebp + 24]);
			c.push(__dword_ptr[ebp + 20]);
			c.push(__dword_ptr[ebp + 16]);
			c.push(__dword_ptr[ebp + 12]);
			c.push(__dword_ptr[ebp + 8]);
			c.call(originalFunctionAddress);

			c.test(eax, eax);
			c.js(done);
			c.mov(edx, __dword_ptr[ebp + 12]);
			c.cmp(edx, ProcessDebugPort);
			c.je(debugPort);
			c.cmp(edx, ProcessDebugObjectHandle);
			c.je(debugObject);
			c.cmp(edx, ProcessDebugFlags);
			c.je(debugFlags);
			c.jmp(done);

			c.Label(ref debugPort);
			c.mov(ecx, __dword_ptr[ebp + 16]);
			c.test(ecx, ecx);
			c.jz(done);
			c.mov(__dword_ptr[ecx], 0);
			c.xor(eax, eax);
			c.jmp(done);

			c.Label(ref debugObject);
			c.mov(ecx, __dword_ptr[ebp + 16]);
			c.test(ecx, ecx);
			c.jz(portNotSet);
			c.mov(__dword_ptr[ecx], 0);

			c.Label(ref portNotSet);
			c.mov(eax, STATUS_PORT_NOT_SET);
			c.jmp(done);

			c.Label(ref debugFlags);
			c.mov(ecx, __dword_ptr[ebp + 16]);
			c.test(ecx, ecx);
			c.jz(done);
			c.mov(__dword_ptr[ecx], 1);
			c.xor(eax, eax);

			c.Label(ref done);
			c.leave();
			c.ret(20);
		}

		public static void AssembleX64(Assembler c, ulong originalFunctionAddress) {
			var done = c.CreateLabel();
			var debugPort = c.CreateLabel();
			var debugObject = c.CreateLabel();
			var portNotSet = c.CreateLabel();
			var debugFlags = c.CreateLabel();

			c.push(rbp);
			c.mov(rbp, rsp);
			c.sub(rsp, 0x60);
			c.mov(__qword_ptr[rbp - 8], rdx);
			c.mov(__qword_ptr[rbp - 16], r8);
			c.mov(__qword_ptr[rbp - 24], r9);
			c.mov(rax, __qword_ptr[rbp + 0x30]);
			c.mov(__qword_ptr[rsp + 0x20], rax);
			c.mov(r10, originalFunctionAddress);
			c.call(r10);

			c.test(eax, eax);
			c.js(done);
			c.mov(rdx, __qword_ptr[rbp - 8]);
			c.cmp(edx, ProcessDebugPort);
			c.je(debugPort);
			c.cmp(edx, ProcessDebugObjectHandle);
			c.je(debugObject);
			c.cmp(edx, ProcessDebugFlags);
			c.je(debugFlags);
			c.jmp(done);

			c.Label(ref debugPort);
			c.mov(r8, __qword_ptr[rbp - 16]);
			c.test(r8, r8);
			c.jz(done);
			c.mov(__qword_ptr[r8], 0);
			c.xor(eax, eax);
			c.jmp(done);

			c.Label(ref debugObject);
			c.mov(r8, __qword_ptr[rbp - 16]);
			c.test(r8, r8);
			c.jz(portNotSet);
			c.mov(__qword_ptr[r8], 0);

			c.Label(ref portNotSet);
			c.mov(eax, STATUS_PORT_NOT_SET);
			c.jmp(done);

			c.Label(ref debugFlags);
			c.mov(r8, __qword_ptr[rbp - 16]);
			c.test(r8, r8);
			c.jz(done);
			c.mov(__dword_ptr[r8], 1);
			c.xor(eax, eax);

			c.Label(ref done);
			c.leave();
			c.ret();
		}
	}
}
