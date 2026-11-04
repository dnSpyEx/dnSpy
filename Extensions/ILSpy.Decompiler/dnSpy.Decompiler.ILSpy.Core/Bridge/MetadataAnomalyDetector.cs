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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using dnlib.DotNet;
using dnlib.DotNet.MD;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Decides whether the upstream decompiler (System.Reflection.Metadata) can safely read a module's original image.
	/// Obfuscators use metadata tricks (duplicate or unusual streams, ENC tables, extra data...) where SRM may see
	/// different metadata than the CLR and dnlib. dnSpy shows what dnlib sees, so any disagreement means the module
	/// must be normalized (rewritten by dnlib) before it's decompiled.
	/// </summary>
	static class MetadataAnomalyDetector {
		const int MaxReported = 8;

		static readonly HashSet<string> standardStreams = new HashSet<string>(StringComparer.Ordinal) {
			"#~", "#Strings", "#US", "#GUID", "#Blob",
			// Written by some compilers/tools, harmless
			"#JTD", "#Pdb",
		};

		/// <summary>
		/// Checks the metadata headers and tables stream without reading the image with SRM
		/// </summary>
		public static List<string> GetStructuralAnomalies(ModuleDefMD module) {
			var result = new List<string>();
			var md = module.Metadata;
			if (!md.IsCompressed)
				result.Add("Uncompressed (#-) tables stream");

			var seen = new HashSet<string>(StringComparer.Ordinal);
			foreach (var stream in md.AllStreams) {
				var name = stream.Name ?? string.Empty;
				if (!seen.Add(name))
					result.Add($"Duplicate metadata stream '{Escape(name)}'");
				else if (!standardStreams.Contains(name) && name != "#-")
					result.Add($"Non-standard metadata stream '{Escape(name)}'");
			}

			var tables = md.TablesStream;
			if (tables.HasExtraData)
				result.Add("Tables stream has extra data");
			if (tables.HasDelete)
				result.Add("Tables stream has the HasDelete flag");
			if (tables.HasDeltaOnly)
				result.Add("Tables stream has the DeltaOnly flag");
			if (tables.ENCLogTable.Rows != 0 || tables.ENCMapTable.Rows != 0)
				result.Add("Edit-and-continue tables have rows");
			return Truncate(result);
		}

		/// <summary>
		/// Compares what SRM reads from the original image with what dnlib read
		/// </summary>
		public static List<string> CompareWithDnlib(ModuleDefMD module, MetadataReader reader) {
			var result = new List<string>();
			var tables = module.Metadata.TablesStream;

			for (int i = 0; i <= (int)Table.GenericParamConstraint; i++) {
				var mdTable = tables.Get((Table)i);
				int dnlibRows = mdTable is null ? 0 : (int)mdTable.Rows;
				int srmRows = reader.GetTableRowCount((TableIndex)i);
				if (dnlibRows != srmRows)
					result.Add($"Row count mismatch in table {(Table)i}: dnlib={dnlibRows}, SRM={srmRows}");
			}

			CompareHeap(result, "#Strings", module.Metadata.StringsStream, reader.GetHeapSize(HeapIndex.String));
			CompareHeap(result, "#Blob", module.Metadata.BlobStream, reader.GetHeapSize(HeapIndex.Blob));
			CompareHeap(result, "#GUID", module.Metadata.GuidStream, reader.GetHeapSize(HeapIndex.Guid));
			CompareHeap(result, "#US", module.Metadata.USStream, reader.GetHeapSize(HeapIndex.UserString));
			if (result.Count != 0)
				return Truncate(result);

			foreach (var handle in reader.TypeDefinitions) {
				var srm = reader.GetTypeDefinition(handle);
				var dnlib = module.ResolveTypeDef((uint)MetadataTokens.GetRowNumber(handle));
				if (dnlib is null) {
					result.Add($"dnlib can't resolve type 0x{MetadataTokens.GetToken(handle):X8}");
					continue;
				}
				if (!Same(reader.GetString(srm.Name), dnlib.Name) || !Same(reader.GetString(srm.Namespace), dnlib.Namespace))
					result.Add($"Type name mismatch at 0x{dnlib.MDToken.Raw:X8}");
				// Which methods/fields belong to the type and which type it's nested in come from table lookups
				// (list columns, sorted NestedClass table) that readers can resolve differently
				uint rid = dnlib.MDToken.Rid;
				if (!SameRids(srm.GetMethods(), module.Metadata.GetMethodRidList(rid)))
					result.Add($"Methods of type 0x{dnlib.MDToken.Raw:X8} differ");
				if (!SameRids(srm.GetFields(), module.Metadata.GetFieldRidList(rid)))
					result.Add($"Fields of type 0x{dnlib.MDToken.Raw:X8} differ");
				var srmDeclaring = srm.GetDeclaringType();
				uint srmDeclaringRid = srmDeclaring.IsNil ? 0 : (uint)MetadataTokens.GetRowNumber(srmDeclaring);
				if (srmDeclaringRid != (dnlib.DeclaringType?.MDToken.Rid ?? 0))
					result.Add($"Declaring type of 0x{dnlib.MDToken.Raw:X8} differs");
				if (result.Count >= MaxReported)
					return Truncate(result);
			}

			foreach (var handle in reader.MethodDefinitions) {
				var srm = reader.GetMethodDefinition(handle);
				var dnlib = module.ResolveMethod((uint)MetadataTokens.GetRowNumber(handle));
				if (dnlib is null) {
					result.Add($"dnlib can't resolve method 0x{MetadataTokens.GetToken(handle):X8}");
					continue;
				}
				if (!Same(reader.GetString(srm.Name), dnlib.Name))
					result.Add($"Method name mismatch at 0x{dnlib.MDToken.Raw:X8}");
				if ((uint)srm.RelativeVirtualAddress != (uint)dnlib.RVA)
					result.Add($"Method RVA mismatch at 0x{dnlib.MDToken.Raw:X8}");
				if (result.Count >= MaxReported)
					return Truncate(result);
			}

			foreach (var handle in reader.FieldDefinitions) {
				var srm = reader.GetFieldDefinition(handle);
				var dnlib = module.ResolveField((uint)MetadataTokens.GetRowNumber(handle));
				if (dnlib is null) {
					result.Add($"dnlib can't resolve field 0x{MetadataTokens.GetToken(handle):X8}");
					continue;
				}
				if (!Same(reader.GetString(srm.Name), dnlib.Name))
					result.Add($"Field name mismatch at 0x{dnlib.MDToken.Raw:X8}");
				if (result.Count >= MaxReported)
					return Truncate(result);
			}

			return Truncate(result);
		}

		static void CompareHeap(List<string> result, string name, DotNetStream? dnlibStream, int srmSize) {
			long dnlibSize = dnlibStream?.StreamLength ?? 0;
			if (srmSize == dnlibSize)
				return;
			// SRM ignores trailing zero bytes of #Strings (padding), dnlib reports the size in the stream header
			if (dnlibStream is not null && srmSize < dnlibSize && IsZero(dnlibStream, (uint)srmSize))
				return;
			result.Add($"Heap size mismatch in {name}: dnlib={dnlibSize}, SRM={srmSize}");
		}

		static bool IsZero(DotNetStream stream, uint start) {
			var reader = stream.CreateReader();
			reader.Position = start;
			while (reader.Position < reader.Length) {
				if (reader.ReadByte() != 0)
					return false;
			}
			return true;
		}

		static bool SameRids<THandle>(IReadOnlyCollection<THandle> srm, RidList dnlib) where THandle : struct {
			if (srm.Count != dnlib.Count)
				return false;
			int i = 0;
			foreach (var handle in srm) {
				int row = handle switch {
					MethodDefinitionHandle m => MetadataTokens.GetRowNumber(m),
					FieldDefinitionHandle f => MetadataTokens.GetRowNumber(f),
					_ => -1,
				};
				if ((uint)row != dnlib[i++])
					return false;
			}
			return true;
		}

		static bool Same(string srm, UTF8String? dnlib) => StringComparer.Ordinal.Equals(srm, UTF8String.ToSystemStringOrEmpty(dnlib));

		static List<string> Truncate(List<string> list) {
			if (list.Count > MaxReported) {
				int more = list.Count - MaxReported;
				list.RemoveRange(MaxReported, list.Count - MaxReported);
				list.Add($"... and {more} more");
			}
			return list;
		}

		static string Escape(string s) {
			var chars = s.ToCharArray();
			for (int i = 0; i < chars.Length; i++) {
				if (char.IsControl(chars[i]))
					chars[i] = '?';
			}
			return new string(chars);
		}
	}
}
