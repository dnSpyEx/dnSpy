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
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;

namespace dnSpy.MainApp {
	/// <summary>
	/// Starts the dnSpy build that matches the bitness of the files passed on the command
	/// line. The shell always starts the exe that was configured in the file association,
	/// so dnSpy.exe can be started for a 32-bit only file (or dnSpy-x86.exe for a 64-bit
	/// only file). When that happens this class starts the other build and exits.
	/// </summary>
	static class StartupBitnessCheck {
		const string NoBitnessCheckArg = "--no-bitness-check";
		const string DnSpyX64Filename = "dnSpy.exe";
		const string DnSpyX86Filename = "dnSpy-x86.exe";

		enum FileBitness {
			AnyCpu,
			X86,
			X86Preferred,
			X64,
		}

		enum RequiredBitness {
			None,
			X86,
			X64,
		}

		/// <summary>
		/// Returns true if another dnSpy process was started and the current process should exit.
		/// </summary>
		public static bool TryRelaunch(AppCommandLineArgs args) {
			if (!args.LoadFiles || args.HasArgument(NoBitnessCheckArg))
				return false;

			var filenames = args.Filenames.ToArray();
			if (filenames.Length == 0)
				return false;

			var requiredBitness = GetRequiredBitness(filenames);
			if (requiredBitness == RequiredBitness.None)
				return false;

			bool wantX64 = requiredBitness == RequiredBitness.X64;
			if (wantX64 == Environment.Is64BitProcess)
				return false;
			// A 32-bit OS can't run the 64-bit build.
			if (wantX64 && !Environment.Is64BitOperatingSystem)
				return false;

			var filename = GetExecutablePath(wantX64);
			if (filename is null)
				return false;

			try {
				var processStartInfo = new ProcessStartInfo {
					FileName = filename,
					Arguments = CreateArguments(),
					UseShellExecute = false,
				};
				Process.Start(processStartInfo);
				return true;
			}
			catch {
				// If the other build couldn't be started, continue with the current process.
				return false;
			}
		}

		static string CreateArguments() {
			var args = new List<string> {
				NoBitnessCheckArg,
			};
			args.AddRange(Environment.GetCommandLineArgs().Skip(1));
			return string.Join(" ", args.Select(QuoteArgument));
		}

		static string QuoteArgument(string arg) {
			if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
				return arg;

			var sb = new System.Text.StringBuilder();
			sb.Append('"');
			int backslashes = 0;
			foreach (var c in arg) {
				if (c == '\\') {
					backslashes++;
					continue;
				}
				if (c == '"') {
					sb.Append('\\', backslashes * 2 + 1);
					sb.Append('"');
					backslashes = 0;
					continue;
				}
				if (backslashes > 0) {
					sb.Append('\\', backslashes);
					backslashes = 0;
				}
				sb.Append(c);
			}
			if (backslashes > 0)
				sb.Append('\\', backslashes * 2);
			sb.Append('"');
			return sb.ToString();
		}

		static RequiredBitness GetRequiredBitness(IEnumerable<string> filenames) {
			bool requiresX86 = false;
			bool requiresX64 = false;
			bool prefersX86 = false;
			foreach (var filename in filenames) {
				if (!TryGetBitness(filename, out var bitness))
					continue;
				switch (bitness) {
				case FileBitness.X86:
					requiresX86 = true;
					break;
				case FileBitness.X86Preferred:
					prefersX86 = true;
					break;
				case FileBitness.X64:
					requiresX64 = true;
					break;
				}
			}

			// No process can load both a 32-bit only and a 64-bit only file.
			if (requiresX86 && requiresX64)
				return RequiredBitness.None;
			if (requiresX64)
				return RequiredBitness.X64;
			if (requiresX86)
				return RequiredBitness.X86;
			if (prefersX86)
				return RequiredBitness.X86;
			return RequiredBitness.None;
		}

		static bool TryGetBitness(string filename, out FileBitness bitness) {
			bitness = FileBitness.AnyCpu;
			try {
				using (var stream = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) {
					if (stream.Length < 0x40)
						return false;
					var reader = new BinaryReader(stream);

					if (reader.ReadUInt16() != 0x5A4D)	// 'MZ'
						return false;
					stream.Position = 0x3C;
					int peHeaderOffset = reader.ReadInt32();
					if (peHeaderOffset < 0x40 || peHeaderOffset > stream.Length - 24)
						return false;

					stream.Position = peHeaderOffset;
					if (reader.ReadUInt32() != 0x00004550)	// 'PE\0\0'
						return false;

					ushort machine = reader.ReadUInt16();
					ushort numberOfSections = reader.ReadUInt16();
					stream.Position += 12;	// TimeDateStamp, PointerToSymbolTable, NumberOfSymbols
					ushort sizeOfOptionalHeader = reader.ReadUInt16();
					stream.Position += 2;	// Characteristics

					long optionalHeaderOffset = stream.Position;
					ushort magic = reader.ReadUInt16();
					bool isPE32Plus;
					if (magic == 0x20B)
						isPE32Plus = true;
					else if (magic == 0x10B)
						isPE32Plus = false;
					else
						return false;

					// NumberOfRvaAndSizes is located just before the data directories.
					int dataDirectoriesOffset = isPE32Plus ? 112 : 96;
					if (sizeOfOptionalHeader < dataDirectoriesOffset + 15 * 8)
						return false;
					stream.Position = optionalHeaderOffset + dataDirectoriesOffset - 4;
					if (reader.ReadUInt32() <= 14)	// No CLR runtime header, not a managed file
						return false;

					stream.Position = optionalHeaderOffset + dataDirectoriesOffset + 14 * 8;
					uint clrHeaderRva = reader.ReadUInt32();
					uint clrHeaderSize = reader.ReadUInt32();
					if (clrHeaderRva == 0 || clrHeaderSize < 20)
						return false;

					long sectionHeadersOffset = peHeaderOffset + 24L + sizeOfOptionalHeader;
					long clrHeaderOffset = -1;
					for (int i = 0; i < numberOfSections; i++) {
						long sectionOffset = sectionHeadersOffset + i * 40L;
						if (sectionOffset + 40 > stream.Length)
							return false;
						stream.Position = sectionOffset + 8;	// Skip section name
						uint virtualSize = reader.ReadUInt32();
						uint virtualAddress = reader.ReadUInt32();
						uint sizeOfRawData = reader.ReadUInt32();
						uint pointerToRawData = reader.ReadUInt32();
						ulong sectionSize = Math.Max(virtualSize, sizeOfRawData);
						if (clrHeaderRva >= virtualAddress && clrHeaderRva < (ulong)virtualAddress + sectionSize) {
							clrHeaderOffset = pointerToRawData + (long)(clrHeaderRva - virtualAddress);
							break;
						}
					}
					if (clrHeaderOffset < 0 || clrHeaderOffset + 20 > stream.Length)
						return false;

					stream.Position = clrHeaderOffset + 16;
					uint corFlags = reader.ReadUInt32();

					const uint COMIMAGE_FLAGS_ILONLY = 0x1;
					const uint COMIMAGE_FLAGS_32BITREQUIRED = 0x2;
					const uint COMIMAGE_FLAGS_32BITPREFERRED = 0x20000;

					if (isPE32Plus) {
						bitness = FileBitness.X64;
						return true;
					}

					if (machine == 0x8664 || machine == 0xAA64 || machine == 0x0200) {
						// AMD64, ARM64, IA64
						bitness = FileBitness.X64;
						return true;
					}
					if ((corFlags & COMIMAGE_FLAGS_32BITREQUIRED) != 0) {
						bitness = FileBitness.X86;
						return true;
					}
					if ((corFlags & COMIMAGE_FLAGS_32BITPREFERRED) != 0) {
						bitness = FileBitness.X86Preferred;
						return true;
					}
					if (machine == 0x014C) {
						// Mixed-mode x86 assemblies can't be loaded by a 64-bit process.
						bitness = (corFlags & COMIMAGE_FLAGS_ILONLY) != 0 ? FileBitness.AnyCpu : FileBitness.X86;
						return true;
					}
					if (machine == 0x01C0 || machine == 0x01C2 || machine == 0x01C4) {
						// ARM, THUMB, ARMNT
						bitness = FileBitness.X86;
						return true;
					}
				}
			}
			catch {
			}
			return false;
		}

		static string? GetExecutablePath(bool wantX64) {
			var currentFilename = GetCurrentExecutablePath();
			if (currentFilename is null)
				return null;

			var envVar = wantX64 ? "DNSPY_X64_PATH" : "DNSPY_X86_PATH";
			var envFilename = Environment.GetEnvironmentVariable(envVar);
			if (IsUsableExecutable(envFilename, currentFilename))
				return envFilename;

			var currentDir = Path.GetDirectoryName(currentFilename);
			if (currentDir is null)
				return null;

			// .NET Framework build: dnSpy.exe and dnSpy-x86.exe are in the same directory.
			var filename = Path.Combine(currentDir, wantX64 ? DnSpyX64Filename : DnSpyX86Filename);
			if (IsUsableExecutable(filename, currentFilename))
				return filename;

			// Self-contained builds use dnSpy.exe in win-x86\publish / win-x64\publish or
			// in the dnSpy-net-win32 / dnSpy-net-win64 release directories.
			var parentDir = Path.GetDirectoryName(currentDir);
			var grandParentDir = parentDir is null ? null : Path.GetDirectoryName(parentDir);
			foreach (var baseDir in new[] { parentDir, grandParentDir }) {
				if (baseDir is null)
					continue;
				var ridDir = wantX64 ? "win-x64" : "win-x86";
				var releaseDir = wantX64 ? "dnSpy-net-win64" : "dnSpy-net-win32";
				foreach (var dir in new[] {
					Path.Combine(baseDir, ridDir, "publish"),
					Path.Combine(baseDir, ridDir),
					Path.Combine(baseDir, releaseDir),
					Path.Combine(baseDir, releaseDir, "publish"),
				}) {
					filename = Path.Combine(dir, DnSpyX64Filename);
					if (IsUsableExecutable(filename, currentFilename))
						return filename;
				}
			}

			return null;
		}

		static bool IsUsableExecutable(string? filename, string currentFilename) {
			if (string.IsNullOrEmpty(filename) || !File.Exists(filename))
				return false;
			try {
				return !string.Equals(Path.GetFullPath(filename), Path.GetFullPath(currentFilename), StringComparison.OrdinalIgnoreCase);
			}
			catch {
				return false;
			}
		}

		static string? GetCurrentExecutablePath() {
			try {
				using (var process = Process.GetCurrentProcess())
					return process.MainModule?.FileName;
			}
			catch {
			}
			try {
				var args = Environment.GetCommandLineArgs();
				if (args.Length > 0)
					return args[0];
			}
			catch {
			}
			return null;
		}
	}
}
