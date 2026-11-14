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
using System.ComponentModel;
using System.Windows;
using dnSpy.Contracts.Hex.Editor;
using Microsoft.VisualStudio.Composition;
using VSUTIL = Microsoft.VisualStudio.Utilities;

namespace dnSpy.Hex.MEF {

	// All interfaces must be public or MEF will complain

	[MetadataView]
	public partial interface ITextViewRoleMetadata {
		IEnumerable<string> TextViewRoles { get; }
	}

	public interface INameAndReplacesMetadata {
		[DefaultValue(null)]
		string Name { get; }

		[DefaultValue(null)]
		IEnumerable<string> Replaces { get; }
	}

	[MetadataView]
	public partial interface IAdornmentLayersMetadata : VSUTIL.IOrderable {
		[DefaultValue(false)]
		bool IsOverlayLayer { get; }

		[DefaultValue(HexLayerKind.Normal)]
		HexLayerKind LayerKind { get; }
	}

	[MetadataView]
	public partial interface IOrderableTextViewRoleMetadata : ITextViewRoleMetadata, VSUTIL.IOrderable {
	}

	public interface ITaggerMetadata {
		IEnumerable<Type> TagTypes { get; }
	}

	[MetadataView]
	public partial interface INamedTaggerMetadata : ITaggerMetadata, INameAndReplacesMetadata {
	}

	[MetadataView]
	public partial interface IViewTaggerMetadata : INamedTaggerMetadata {
		[DefaultValue(null)]
		IEnumerable<string> TextViewRoles { get; }
	}

	[MetadataView]
	public partial interface IWpfHexViewMarginMetadata : IOrderableTextViewRoleMetadata {
		string MarginContainer { get; }

		[DefaultValue(1.0)]
		double GridCellLength { get; }

		[DefaultValue(GridUnitType.Auto)]
		GridUnitType GridUnitType { get; }

		[DefaultValue(null)]
		string OptionName { get; }

		[DefaultValue(null)]
		IEnumerable<string> Replaces { get; }
	}

	[MetadataView]
	public partial interface IDeferrableTextViewRoleMetadata : ITextViewRoleMetadata {
		[DefaultValue(null)]
		string OptionName { get; }
	}

	public interface IGlyphMarginMetadata {
		[DefaultValue(null)]
		IEnumerable<string> GlyphMargins { get; }
	}

	[MetadataView]
	public partial interface IGlyphMouseProcessorProviderMetadata : IGlyphMarginMetadata, VSUTIL.IOrderable {
	}

	[MetadataView]
	public partial interface IGlyphMetadata : ITaggerMetadata, VSUTIL.IOrderable {
	}

	[MetadataView]
	public partial interface IMarginContextMenuHandlerProviderMetadata {
		[DefaultValue(null)]
		IEnumerable<string> TextViewRoles { get; }

		string MarginName { get; }
	}
}
