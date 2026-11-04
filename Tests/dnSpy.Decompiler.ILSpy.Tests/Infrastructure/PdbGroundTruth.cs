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
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>
	/// What the C# compiler itself recorded for a method in its PDB. The IL is identical to what the decompiler
	/// sees, so this is ground truth for IL offsets: breakpoint locations, local slots and async stepping.
	/// </summary>
	sealed class PdbMethodInfo {
		public int Token { get; }
		public List<int> SequencePointOffsets { get; } = new List<int>();
		public List<int> HiddenSequencePointOffsets { get; } = new List<int>();

		/// <summary>
		/// Source location (1-based lines/columns in the original source) of each non-hidden sequence point
		/// </summary>
		public Dictionary<int, (int startLine, int startColumn, int endLine, int endColumn)> SourceLocations { get; } = new Dictionary<int, (int, int, int, int)>();
		public Dictionary<int, string> LocalNames { get; } = new Dictionary<int, string>();
		public List<(int yieldOffset, int resumeOffset)> Awaits { get; } = new List<(int, int)>();
		public int? CatchHandlerOffset { get; set; }
		public int KickoffMethodToken { get; set; }

		public PdbMethodInfo(int token) => Token = token;
	}

	static class PdbGroundTruth {
		static readonly Guid asyncMethodSteppingInformation = new Guid("54FD2AC5-E925-401A-9C2A-F94F171072F8");

		public static Dictionary<int, PdbMethodInfo> Read(byte[] pdb) {
			var result = new Dictionary<int, PdbMethodInfo>();
			using var provider = MetadataReaderProvider.FromPortablePdbImage(ImmutableArray.Create(pdb));
			var reader = provider.GetMetadataReader();
			foreach (var handle in reader.MethodDebugInformation) {
				var methodHandle = handle.ToDefinitionHandle();
				var info = new PdbMethodInfo(MetadataTokens.GetToken(methodHandle));
				var mdi = reader.GetMethodDebugInformation(handle);
				foreach (var sp in mdi.GetSequencePoints()) {
					if (sp.IsHidden)
						info.HiddenSequencePointOffsets.Add(sp.Offset);
					else {
						info.SequencePointOffsets.Add(sp.Offset);
						info.SourceLocations[sp.Offset] = (sp.StartLine, sp.StartColumn, sp.EndLine, sp.EndColumn);
					}
				}
				var kickoff = mdi.GetStateMachineKickoffMethod();
				if (!kickoff.IsNil)
					info.KickoffMethodToken = MetadataTokens.GetToken(kickoff);

				foreach (var scopeHandle in reader.GetLocalScopes(methodHandle)) {
					foreach (var localHandle in reader.GetLocalScope(scopeHandle).GetLocalVariables()) {
						var local = reader.GetLocalVariable(localHandle);
						if ((local.Attributes & LocalVariableAttributes.DebuggerHidden) != 0)
							continue;
						info.LocalNames[local.Index] = reader.GetString(local.Name);
					}
				}

				foreach (var cdiHandle in reader.GetCustomDebugInformation(methodHandle)) {
					var cdi = reader.GetCustomDebugInformation(cdiHandle);
					if (reader.GetGuid(cdi.Kind) != asyncMethodSteppingInformation)
						continue;
					var blob = reader.GetBlobReader(cdi.Value);
					// Catch handler offset + 1, or 0 if there's no catch handler
					uint catchHandler = blob.ReadUInt32();
					if (catchHandler != 0)
						info.CatchHandlerOffset = (int)(catchHandler - 1);
					while (blob.RemainingBytes > 0) {
						int yieldOffset = (int)blob.ReadUInt32();
						int resumeOffset = (int)blob.ReadUInt32();
						blob.ReadCompressedInteger(); // resume method rid
						info.Awaits.Add((yieldOffset, resumeOffset));
					}
				}

				if (info.SequencePointOffsets.Count != 0 || info.Awaits.Count != 0)
					result[info.Token] = info;
			}
			return result;
		}
	}
}
