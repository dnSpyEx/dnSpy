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
using System.Globalization;
using System.IO;
using System.Runtime.Serialization.Formatters.Binary;
using dnlib.DotNet.Resources;
using dnSpy.Contracts.DnSpy.Properties;

namespace dnSpy.Contracts.Documents.TreeView.Resources {
	/// <summary>
	/// Serialization utilities
	/// </summary>
	public static class SerializationUtilities {
		/// <summary>
		/// Creates a serialized image
		/// </summary>
		/// <param name="filename">Filename of image</param>
		/// <returns></returns>
		public static ResourceElement CreateSerializedImage(string filename) {
			using (var stream = File.OpenRead(filename))
				return CreateSerializedImage(stream, filename);
		}

		static ResourceElement CreateSerializedImage(Stream stream, string filename) {
			object obj;
			string typeName;
			if (filename.EndsWith(".ico", StringComparison.OrdinalIgnoreCase)) {
				obj = new System.Drawing.Icon(stream);
				typeName = SerializedImageUtilities.SystemDrawingIcon.AssemblyQualifiedName;
			}
			else {
				obj = new System.Drawing.Bitmap(stream);
				typeName = SerializedImageUtilities.SystemDrawingBitmap.AssemblyQualifiedName;
			}
			var serializedData = Serialize(obj);

			var userType = new UserResourceType(typeName, ResourceTypeCode.UserTypes);
			var rsrcElem = new ResourceElement {
				Name = Path.GetFileName(filename),
				ResourceData = new BinaryResourceData(userType, serializedData, SerializationFormat.BinaryFormatter),
			};

			return rsrcElem;
		}

		/// <summary>
		/// Serializes the object
		/// </summary>
		/// <param name="obj">Data</param>
		/// <returns></returns>
		public static byte[] Serialize(object? obj) {
			if (obj is null)
				return Array.Empty<byte>();

			//TODO: The asm names of the saved types are saved in the serialized data. If the current
			//		module is eg. a .NET 2.0 asm, you should replace the versions from 4.0.0.0 to 2.0.0.0.
#pragma warning disable SYSLIB0011
			var formatter = new BinaryFormatter();
			var outStream = new MemoryStream();
			formatter.Serialize(outStream, obj);
#pragma warning restore SYSLIB0011
			return outStream.ToArray();
		}
	}
}
