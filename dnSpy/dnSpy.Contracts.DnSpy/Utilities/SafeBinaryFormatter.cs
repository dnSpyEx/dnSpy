using System;
using System.Formats.Nrbf;
using System.IO;

namespace dnSpy.Contracts.Utilities {
	/// <summary>
	/// Provide safe utility methods to deserialize and serialize data using the BinaryFormatter format.
	/// </summary>
	public static class SafeBinaryFormatter {
		/// <summary>
		/// Extracts a byte array field from a serialized object
		/// </summary>
		/// <param name="data">The data blob</param>
		/// <param name="typeName">The expected type name of the serialized object.</param>
		/// <param name="fieldName">The field name to extract</param>
		/// <param name="ignoreCase">Set to <c>true</c> if the case of <see cref="fieldName"/> should be ignored.</param>
		/// <returns></returns>
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
	}
}
