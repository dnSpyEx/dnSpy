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
using System.Text;
using dnSpy.Contracts.Documents.Tabs.DocViewer.ToolTips;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.Text;
using dnSpy.Properties;

namespace dnSpy.Documents.Tabs.DocViewer.ToolTips {
	[ExportDocumentViewerToolTipProvider]
	sealed class NumberDocumentViewerToolTipProvider : IDocumentViewerToolTipProvider {
		public object? Create(IDocumentViewerToolTipProviderContext context, object? @ref) {
			switch (@ref) {
			case sbyte value:
				return CreateSByte(context, value);
			case byte value:
				return CreateByte(context, value);
			case short value:
				return CreateInt16(context, value);
			case ushort value:
				return CreateUInt16(context, value);
			case int value:
				return CreateInt32(context, value);
			case uint value:
				return CreateUInt32(context, value);
			case long value:
				return CreateInt64(context, value);
			case ulong value:
				return CreateUInt64(context, value);
			case float value:
				return CreateSingle(context, value);
			case double value:
				return CreateDouble(context, value);
			}
			return null;
		}

		static readonly (int @base, int groupSize, string groupSeparator)[] numberBases = new (int, int, string)[] {
			(2,  4, " "),
			(10, 3, "_"),
			(16, 0, string.Empty),
		};
		object Create(IDocumentViewerToolTipProviderContext context, Func<StringBuilder, int, string> toBase) {
			var provider = context.Create();
			provider.Image = DsImages.ConstantPublic;

			bool needNewline = false;
			var sb = new StringBuilder();
			foreach (var info in numberBases) {
				if (needNewline)
					provider.Output.WriteLine();
				needNewline = true;

				provider.Output.Write(BoxedTextColor.Text, string.Format(dnSpy_Resources.NumberBaseFormatString, info.@base));
				provider.Output.Write(BoxedTextColor.Text, " ");
				var numStr = toBase(sb, info.@base);
				if (info.groupSize != 0)
					numStr = NumberUtils.AddDigitSeparators(sb, numStr, info.groupSize, info.groupSeparator);
				provider.Output.Write(BoxedTextColor.Number, numStr);
			}

			return provider.Create();
		}

		object CreateSByte(IDocumentViewerToolTipProviderContext context, sbyte value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(sbyte)));
		object CreateByte(IDocumentViewerToolTipProviderContext context, byte value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(byte)));
		object CreateInt16(IDocumentViewerToolTipProviderContext context, short value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(short)));
		object CreateUInt16(IDocumentViewerToolTipProviderContext context, ushort value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(ushort)));
		object CreateInt32(IDocumentViewerToolTipProviderContext context, int value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(int)));
		object CreateUInt32(IDocumentViewerToolTipProviderContext context, uint value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(uint)));
		object CreateInt64(IDocumentViewerToolTipProviderContext context, long value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(long)));
		object CreateUInt64(IDocumentViewerToolTipProviderContext context, ulong value) => Create(context, (sb, n) => NumberUtils.ToString(sb, n, value, sizeof(ulong)));

		object CreateFloat(IDocumentViewerToolTipProviderContext context, string valueStr, string serializedFormatStr, string serializedValueStr, Func<StringBuilder, string> getRawValue) {
			var provider = context.Create();
			provider.Image = DsImages.ConstantPublic;
				
			var sb = new StringBuilder();

			provider.Output.Write(BoxedTextColor.Number, valueStr);
			provider.Output.WriteLine();

			provider.Output.Write(BoxedTextColor.Text, serializedFormatStr);
			provider.Output.Write(BoxedTextColor.Text, ": ");
			provider.Output.Write(BoxedTextColor.Number, serializedValueStr);
			provider.Output.WriteLine();

			var rawValueStr = getRawValue(sb);
			provider.Output.Write(BoxedTextColor.Text, dnSpy_Resources.RawValue);
			provider.Output.Write(BoxedTextColor.Text, " ");
			provider.Output.Write(BoxedTextColor.Number, rawValueStr);

			return provider.Create();
		}

		object CreateSingle(IDocumentViewerToolTipProviderContext context, float value) {
			const string format = "G9";
			const bool upper = true;
			uint rawValue = BitConverter.ToUInt32(BitConverter.GetBytes(value), 0);
			return CreateFloat(context, value.ToString(), format, value.ToString(format),
				sb => NumberUtils.ToFixedSizeHexadecimalArray(sb, rawValue, 4, upper));
		}

		object CreateDouble(IDocumentViewerToolTipProviderContext context, double value) {
			const string format = "G17";
			const bool upper = true;
			ulong rawValue = BitConverter.ToUInt64(BitConverter.GetBytes(value), 0);
			return CreateFloat(context, value.ToString(), format, value.ToString(format),
				sb => NumberUtils.ToFixedSizeHexadecimalArray(sb, rawValue, 8, upper));
		}
	}
}
