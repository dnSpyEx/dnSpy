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

using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using dnlib.DotNet.Resources;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.TreeView;

namespace dnSpy.Contracts.Documents.TreeView.Resources {
	/// <summary>
	/// Serialized resource element node base class
	/// </summary>
	public abstract class SerializedResourceElementNode : ResourceElementNode {
		/// <inheritdoc/>
		protected override ImageReference GetIcon() => DsImages.UserDefinedDataType;

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="treeNodeGroup">Treenode group</param>
		/// <param name="resourceElement">Resource element</param>
		protected SerializedResourceElementNode(ITreeNodeGroup treeNodeGroup, ResourceElement resourceElement)
			: base(treeNodeGroup, resourceElement) => Debug.Assert(resourceElement.ResourceData is BinaryResourceData);

		/// <inheritdoc/>
		protected override IEnumerable<ResourceData> GetDeserializedData() {
			var re = ResourceElement;
			yield return new ResourceData(re.Name, _ => new MemoryStream(((BinaryResourceData)re.ResourceData).Data));
		}
	}
}
