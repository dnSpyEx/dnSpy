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
using System.Reflection.Metadata.Ecma335;
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;

namespace dnSpy.Decompiler.ILSpy.Core.Bridge {
	/// <summary>
	/// Decides which compiler generated members the tree view hides (they're shown inline in the decompiled code),
	/// using the upstream decompiler's own rules so both agree.
	/// </summary>
	/// <remarks>
	/// The tree view asks for every member on the UI thread, so this only opens the module's original image
	/// (cheap, never rewritten). Members that aren't part of it (added by an edit) are always shown.
	/// </remarks>
	sealed class MemberVisibility {
		readonly ConditionalWeakTable<ModuleDef, ImageHolder> images = new ConditionalWeakTable<ModuleDef, ImageHolder>();

		sealed class ImageHolder {
			public readonly MetadataFile? File;
			public ImageHolder(MetadataFile? file) => File = file;
		}

		public bool IsHidden(IMemberDef member, DecompilerSettings settings) {
			if (!(member.Module is ModuleDefMD module))
				return false;
			var token = member.MDToken;
			if (token.Rid == 0)
				return false;
			var file = images.GetValue(module, m => new ImageHolder(TryOpen((ModuleDefMD)m))).File;
			if (file is null)
				return false;
			try {
				if (!ReferenceEquals(module.ResolveToken(token.Raw), member))
					return false;
				return CSharpDecompiler.MemberIsHidden(file, MetadataTokens.EntityHandle((int)token.Raw), settings);
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException)) {
				// Invalid metadata
				return false;
			}
		}

		static MetadataFile? TryOpen(ModuleDefMD module) {
			try {
				return ModuleBridge.OpenOriginal(module);
			}
			catch (Exception ex) when (!(ex is OutOfMemoryException)) {
				return null;
			}
		}
	}
}
