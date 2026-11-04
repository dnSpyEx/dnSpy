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
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using dnlib.DotNet;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// A copy of a bridged image in which some methods have no body (their MethodDef rows' RVA is 0), so the upstream
	/// decompiler only decompiles their signatures (it shows them as 'extern'). Upstream can only decompile a whole type,
	/// so Edit Method on a member of a big type would otherwise spend most of its time on code that is thrown away. The
	/// method bodies themselves, tokens and everything else stay the same.
	/// </summary>
	static class ImageWithoutMethodBodies {
		/// <summary>
		/// Creates the image, or returns null if there isn't enough code to leave out or the image can't be changed
		/// </summary>
		/// <param name="cache">Cache, the image is registered in it so its entities map back to dnlib</param>
		/// <param name="bridged">Image of the module</param>
		/// <param name="methods">Methods whose bodies are left out</param>
		/// <param name="minSize">Min total size of the bodies (smaller savings aren't worth a new type system)</param>
		public static BridgedModule? TryCreate(ModuleBridgeCache cache, BridgedModule bridged, IEnumerable<MethodDef> methods, int minSize) {
			try {
				return TryCreateCore(cache, bridged, methods, minSize);
			}
			catch (Exception ex) when (ex is BadImageFormatException || ex is InvalidOperationException || ex is ArgumentException || ex is IndexOutOfRangeException || ex is OverflowException) {
				return null;
			}
		}

		static BridgedModule? TryCreateCore(ModuleBridgeCache cache, BridgedModule bridged, IEnumerable<MethodDef> methods, int minSize) {
			if (bridged.File is not PEFile pe)
				return null;
			var reader = pe.Reader;
			if (!reader.IsEntireImageAvailable || !reader.HasMetadata)
				return null;
			var metadata = pe.Metadata;

			var rows = new List<(MethodDefinitionHandle handle, int rva)>();
			var seen = new HashSet<MethodDefinitionHandle>();
			long size = 0;
			foreach (var method in methods) {
				int token = bridged.Tokens.GetToken(method);
				if ((token >> 24) != 0x06 || (token & 0x00FFFFFF) == 0 || (token & 0x00FFFFFF) > metadata.MethodDefinitions.Count)
					continue;
				var handle = MetadataTokens.MethodDefinitionHandle(token & 0x00FFFFFF);
				if (!seen.Add(handle))
					continue;
				int rva = metadata.GetMethodDefinition(handle).RelativeVirtualAddress;
				if (rva == 0)
					continue;
				int bodySize = GetBodySize(reader, rva);
				if (bodySize <= 0)
					continue;
				rows.Add((handle, rva));
				size += bodySize;
			}
			if (rows.Count == 0 || size < minSize)
				return null;

			var image = reader.GetEntireImage();
			var bytes = new byte[image.Length];
			image.GetContent().CopyTo(bytes);
			int metadataStart = reader.PEHeaders.MetadataStartOffset;
			// 'BSJB'
			if (metadataStart < 0 || metadataStart + 4 > bytes.Length || BitConverter.ToUInt32(bytes, metadataStart) != 0x424A5342)
				return null;
			int tableStart = metadataStart + metadata.GetTableMetadataOffset(TableIndex.MethodDef);
			int rowSize = metadata.GetTableRowSize(TableIndex.MethodDef);
			foreach (var (handle, rva) in rows) {
				// The RVA is the first column
				int offset = tableStart + (MetadataTokens.GetRowNumber(handle) - 1) * rowSize;
				if (offset < 0 || offset + 4 > bytes.Length || BitConverter.ToInt32(bytes, offset) != rva)
					return null;
				bytes[offset] = 0;
				bytes[offset + 1] = 0;
				bytes[offset + 2] = 0;
				bytes[offset + 3] = 0;
			}

			var options = PEStreamOptions.PrefetchEntireImage;
			if (reader.IsLoadedImage)
				options |= PEStreamOptions.IsLoadedImage;
			var file = new PEFile(pe.FileName, new MemoryStream(bytes, writable: false), options);
			if (file.Metadata.GetMethodDefinition(rows[0].handle).RelativeVirtualAddress != 0) {
				file.Dispose();
				return null;
			}
			return cache.AddImage(bridged, file);
		}

		static int GetBodySize(PEReader reader, int rva) {
			try {
				return reader.GetMethodBody(rva).Size;
			}
			catch (Exception ex) when (ex is BadImageFormatException || ex is ArgumentException || ex is InvalidOperationException) {
				return -1;
			}
		}
	}
}
