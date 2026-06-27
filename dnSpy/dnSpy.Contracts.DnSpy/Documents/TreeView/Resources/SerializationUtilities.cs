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
using System.Formats.Nrbf;
using System.IO;

namespace dnSpy.Contracts.Documents.TreeView.Resources {
	/// <summary>
	/// Serialization utilities for BinaryFormatter
	/// </summary>
	public static class SerializationUtilities {
		/// <summary>
		/// Extracts a byte array field from a serialized object
		/// </summary>
		/// <param name="data">The data blob</param>
		/// <param name="typeName">The expected type name of the serialized object</param>
		/// <param name="fieldName">The field name to extract</param>
		/// <param name="ignoreCase">Set to <c>true</c> if the case of <paramref name="fieldName"/> should be ignored</param>
		/// <returns>The byte array contained in the field</returns>
		public static byte[]? DeserializeToByteArray(byte[] data, string typeName, string fieldName, bool ignoreCase = false) {
			var rootRecord = NrbfDecoder.Decode(new MemoryStream(data));
			if (rootRecord is not ClassRecord classRecord || classRecord.TypeName.FullName != typeName)
				return null;

			if (ignoreCase) {
				foreach (string memberName in classRecord.MemberNames) {
					if (memberName.Equals(fieldName, StringComparison.OrdinalIgnoreCase)) {
						fieldName = memberName;
						break;
					}
				}
			}

			var arrayRecord = classRecord.GetArrayRecord(fieldName);
			if (arrayRecord is null || arrayRecord.TypeName.FullName != "System.Byte[]")
				return null;
			return (byte[])arrayRecord.GetArray(typeof(byte[]));
		}

		/// <summary>
		/// Serializes an icon
		/// </summary>
		/// <param name="data">The icon data</param>
		/// <param name="size">The icon size</param>
		/// <param name="assemblyName">The assembly name to used in the serialized blob</param>
		/// <returns>The serialized blob for use in BinaryFormatter</returns>
		public static byte[] SerializeIcon(byte[] data, System.Drawing.Size size, string assemblyName) {
			using var memoryStream = new MemoryStream();
			using var writer = new BinaryWriter(memoryStream);

			const int libraryId = 2;
			const int arrayId = 3;

			WriteSerializationHeader(writer);
			WriteBinaryLibrary(writer, libraryId, assemblyName);

			// ClassWithMembersAndTypes
			writer.Write((byte)0x5);
			{
				// ClassInfo
				writer.Write(1);
				writer.Write("System.Drawing.Icon");
				writer.Write(2);
				writer.Write("IconData");
				writer.Write("IconSize");
			}
			{
				// MemberInfo
				writer.Write((byte)BinaryType.PrimitiveArray);
				writer.Write((byte)BinaryType.Class);

				writer.Write((byte)PrimitiveType.Byte);
				writer.Write("System.Drawing.Size");
				writer.Write(libraryId);
			}
			writer.Write(libraryId);
			{
				// MemberReference
				writer.Write((byte)0x9);
				writer.Write(arrayId);

				// ClassWithMembersAndTypes
				writer.Write((byte)0x5);
				{
					// ClassInfo
					writer.Write(-4);
					writer.Write("System.Drawing.Size");
					writer.Write(2);
					writer.Write("width");
					writer.Write("height");
				}
				{
					// MemberInfo
					writer.Write((byte)BinaryType.Primitive);
					writer.Write((byte)BinaryType.Primitive);

					writer.Write((byte)PrimitiveType.Int32);
					writer.Write((byte)PrimitiveType.Int32);
				}
				writer.Write(libraryId);
				{
					writer.Write(size.Width);
					writer.Write(size.Height);
				}
			}

			WriteByteArray(writer, arrayId, data);
			WriteMessageEnd(writer);

			return memoryStream.ToArray();
		}

		/// <summary>
		/// Serializes a bitmap
		/// </summary>
		/// <param name="data">The bitmap data</param>
		/// <param name="assemblyName">The assembly name to used in the serialized blob</param>
		/// <returns>The serialized blob for use in BinaryFormatter</returns>
		public static byte[] SerializeBitmap(byte[] data, string assemblyName) {
			using var memoryStream = new MemoryStream();
			using var writer = new BinaryWriter(memoryStream);

			const int libraryId = 2;
			const int arrayId = 3;

			WriteSerializationHeader(writer);
			WriteBinaryLibrary(writer, libraryId, assemblyName);

			// ClassWithMembersAndTypes
			writer.Write((byte)0x5);
			{
				// ClassInfo
				writer.Write(1);
				writer.Write("System.Drawing.Bitmap");
				writer.Write(1);
				writer.Write("Data");
			}
			{
				// MemberInfo
				writer.Write((byte)BinaryType.PrimitiveArray);
				writer.Write((byte)PrimitiveType.Byte);
			}
			writer.Write(libraryId);
			{
				// MemberReference
				writer.Write((byte)0x9);
				writer.Write(arrayId);
			}

			WriteByteArray(writer, arrayId, data);
			WriteMessageEnd(writer);

			return memoryStream.ToArray();
		}

		/// <summary>
		/// Serializes an image list streamer
		/// </summary>
		/// <param name="data">The image list data</param>
		/// <param name="assemblyName">The assembly name to used in the serialized blob</param>
		/// <returns>The serialized blob for use in BinaryFormatter</returns>
		public static byte[] SerializeImageListStreamer(byte[] data, string assemblyName) {
			using var memoryStream = new MemoryStream();
			using var writer = new BinaryWriter(memoryStream);

			const int libraryId = 2;
			const int arrayId = 3;

			WriteSerializationHeader(writer);
			WriteBinaryLibrary(writer, libraryId, assemblyName);

			// ClassWithMembersAndTypes
			writer.Write((byte)0x5);
			{
				// ClassInfo
				writer.Write(1);
				writer.Write("System.Windows.Forms.ImageListStreamer");
				writer.Write(1);
				writer.Write("Data");
			}
			{
				// MemberInfo
				writer.Write((byte)BinaryType.PrimitiveArray);
				writer.Write((byte)PrimitiveType.Byte);
			}
			writer.Write(libraryId);
			{
				// MemberReference
				writer.Write((byte)0x9);
				writer.Write(arrayId);
			}

			WriteByteArray(writer, arrayId, data);
			WriteMessageEnd(writer);

			return memoryStream.ToArray();
		}

		private static void WriteByteArray(BinaryWriter writer, int id, byte[] data) {
			// ArraySinglePrimitive
			writer.Write((byte)0x0F);
			{
				// ArrayInfo
				writer.Write(id);
				writer.Write(data.Length);
			}
			writer.Write((byte)PrimitiveType.Byte);
			writer.Write(data);
		}

		private static void WriteMessageEnd(BinaryWriter writer) {
			writer.Write((byte)0x0B);
		}

		private static void WriteBinaryLibrary(BinaryWriter writer, int id, string assemblyName) {
			writer.Write((byte)0xC);
			writer.Write(id);
			writer.Write(assemblyName);
		}

		private static void WriteSerializationHeader(BinaryWriter writer) {
			writer.Write((byte)0);
			writer.Write(1);
			writer.Write(-1);
			writer.Write(1);
			writer.Write(0);
		}

		enum BinaryType : byte {
			Primitive,
			String,
			Object,
			SystemClass,
			Class,
			ObjectArray,
			StringArray,
			PrimitiveArray
		}

		enum PrimitiveType : byte {
			Boolean = 1,
			Byte,
			Char,
			Decimal = 5,
			Double,
			Int16,
			Int32,
			Int64,
			SByte,
			Single,
			TimeSpan,
			DateTime,
			UInt16,
			UInt32,
			UInt64,
			Null,
			String
		}
	}
}
