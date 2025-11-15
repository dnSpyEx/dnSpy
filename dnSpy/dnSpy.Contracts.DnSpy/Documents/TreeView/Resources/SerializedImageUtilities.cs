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
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Text;
using dnlib.DotNet;
using dnlib.DotNet.Resources;
using dnSpy.Contracts.Utilities;

namespace dnSpy.Contracts.Documents.TreeView.Resources {
	/// <summary>
	/// Serialized image utilities
	/// </summary>
	public static class SerializedImageUtilities {
		/// <summary>
		/// Gets the image data
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="typeName">Name of type</param>
		/// <param name="serializedData">Serialized data</param>
		/// <param name="imageData">Updated with the image data</param>
		/// <returns></returns>
		public static bool GetImageData(ModuleDef? module, string typeName, byte[] serializedData, [NotNullWhen(true)] out byte[]? imageData) =>
			GetImageData(module, typeName, serializedData, SerializationFormat.BinaryFormatter, out imageData);

		/// <summary>
		/// Gets the image data
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="typeName">Name of type</param>
		/// <param name="serializedData">Serialized data</param>
		/// <param name="format">Format of serialized data</param>
		/// <param name="imageData">Updated with the image data</param>
		/// <returns></returns>
		public static bool GetImageData(ModuleDef? module, string typeName, byte[] serializedData, SerializationFormat format, [NotNullWhen(true)] out byte[]? imageData) {
			imageData = null;

			if (CouldBeBitmap(module, typeName)) {
				if (format == SerializationFormat.BinaryFormatter) {
					imageData = SafeBinaryFormatter.DeserializeToByteArray(serializedData, "System.Drawing.Bitmap", "Data", true);
					return imageData is not null;
				}
				if (format == SerializationFormat.ActivatorStream) {
					imageData = serializedData;
					return true;
				}
				if (format == SerializationFormat.TypeConverterByteArray) {
					imageData = GetBitmapData(serializedData) ?? serializedData;
					return true;
				}
			}

			if (CouldBeIcon(module, typeName)) {
				if (format == SerializationFormat.BinaryFormatter) {
					imageData = SafeBinaryFormatter.DeserializeToByteArray(serializedData, "System.Drawing.Icon", "IconData");
					return imageData is not null;
				}
				if (format == SerializationFormat.ActivatorStream || format == SerializationFormat.TypeConverterByteArray) {
					imageData = serializedData;
					return true;
				}
			}

			return false;
		}

		static byte[]? GetBitmapData(byte[] rawData) {
			// Based on ImageConverter.GetBitmapStream
			// See https://github.com/dotnet/winforms/blob/main/src/System.Drawing.Common/src/System/Drawing/ImageConverter.cs
			if (rawData.Length <= 18)
				return null;

			short sig = (short)(rawData[0] | rawData[1] << 8);
			if (sig != 0x1C15)
				return null;

			short headerSize = (short)(rawData[2] | rawData[3] << 8);
			if (rawData.Length <= headerSize + 18)
				return null;
			if (Encoding.ASCII.GetString(rawData, headerSize + 12, 6) != "PBrush")
				return null;

			var newData = new byte[rawData.Length - 78];
			Buffer.BlockCopy(rawData, 78, newData, 0, newData.Length);
			return newData;
		}

		static bool CouldBeBitmap(ModuleDef? module, string name) => CheckType(module, name, SystemDrawingBitmap);
		static bool CouldBeIcon(ModuleDef? module, string name) => CheckType(module, name, SystemDrawingIcon);

		/// <summary>
		/// Checks whether the type matches an expected type
		/// </summary>
		/// <param name="module">Module</param>
		/// <param name="name">Type name</param>
		/// <param name="expectedType">Expected type</param>
		/// <returns></returns>
		public static bool CheckType(ModuleDef? module, string name, TypeRef expectedType) {
			if (module is null)
				module = new ModuleDefUser();
			var tr = TypeNameParser.ParseReflection(module, name, null);
			if (tr is null)
				return false;

			var flags = AssemblyNameComparerFlags.All & ~AssemblyNameComparerFlags.Version;
			if (!new AssemblyNameComparer(flags).Equals(tr.DefinitionAssembly, expectedType.DefinitionAssembly))
				return false;

			if (!new SigComparer().Equals(tr, expectedType))
				return false;

			return true;
		}
		static readonly AssemblyRef SystemDrawingAsm = new AssemblyRefUser(new AssemblyNameInfo("System.Drawing, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"));
		internal static readonly TypeRef SystemDrawingBitmap = new TypeRefUser(null, "System.Drawing", "Bitmap", SystemDrawingAsm);
		internal static readonly TypeRef SystemDrawingIcon = new TypeRefUser(null, "System.Drawing", "Icon", SystemDrawingAsm);

		/// <summary>
		/// Serializes the image
		/// </summary>
		/// <param name="resElem">Resource element</param>
		/// <returns></returns>
		public static ResourceElement Serialize(ResourceElement resElem) => Serialize(resElem, SerializationFormat.BinaryFormatter);

		/// <summary>
		/// Serializes the image
		/// </summary>
		/// <param name="resElem">Resource element</param>
		/// <param name="format">Serialization format to use</param>
		/// <returns></returns>
		public static ResourceElement Serialize(ResourceElement resElem, SerializationFormat format) {
			var data = (byte[])((BuiltInResourceData)resElem.ResourceData).Data;
			bool isIcon = BitConverter.ToUInt32(data, 0) == 0x00010000;

			object obj;
			string typeName;
			if (isIcon) {
				obj = new System.Drawing.Icon(new MemoryStream(data));
				typeName = SystemDrawingIcon.AssemblyQualifiedName;
			}
			else {
				obj = new System.Drawing.Bitmap(new MemoryStream(data));
				typeName = SystemDrawingBitmap.AssemblyQualifiedName;
			}

			byte[] serializedData;
			if (format == SerializationFormat.BinaryFormatter) {
				serializedData = SerializationUtilities.Serialize(obj);
			}
			else if (format == SerializationFormat.TypeConverterByteArray) {
				var converter = TypeDescriptor.GetConverter(obj.GetType());
				var byteArr = converter.ConvertTo(obj, typeof(byte[]));
				if (byteArr is not byte[] d)
					throw new InvalidOperationException("Failed to serialize image");
				serializedData = d;
			}
			else if (format == SerializationFormat.ActivatorStream) {
				using (var stream = new MemoryStream()) {
					if (obj is System.Drawing.Bitmap bitmap)
						bitmap.Save(stream, bitmap.RawFormat);
					else
						((System.Drawing.Icon)obj).Save(stream);
					serializedData = stream.ToArray();
				}
			}
			else
				throw new ArgumentOutOfRangeException(nameof(format));

			return new ResourceElement {
				Name = resElem.Name,
				ResourceData = new BinaryResourceData(new UserResourceType(typeName, ResourceTypeCode.UserTypes), serializedData, format),
			};
		}
	}
}
