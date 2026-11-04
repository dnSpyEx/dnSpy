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
using System.Globalization;
using System.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// Adds dnSpy's "// Token: 0x06000001 RID: 1 RVA: 0x00002050 File Offset: 0x00000250" comments to all
	/// declarations. The tokens are dnlib's (what the rest of dnSpy shows), not the bridged image's.
	/// </summary>
	sealed class TokenCommentTransform : IAstTransform {
		readonly ModuleBridgeCache cache;

		public TokenCommentTransform(ModuleBridgeCache cache) => this.cache = cache ?? throw new ArgumentNullException(nameof(cache));

		public void Run(AstNode rootNode, TransformContext context) {
			foreach (var entity in rootNode.DescendantsAndSelf.OfType<EntityDeclaration>().ToArray()) {
				if (entity is Accessor || entity.Parent is LocalFunctionDeclarationStatement)
					continue;
				if (!(entity.GetSymbol() is IEntity symbol) || !(cache.ToDnlib(symbol) is IMemberDef member))
					continue;
				Add(entity, member, null);
				switch (member) {
				case PropertyDef property:
					Add(entity, property.GetMethod, "get");
					Add(entity, property.SetMethod, "set");
					break;
				case EventDef ev:
					Add(entity, ev.AddMethod, "add");
					Add(entity, ev.RemoveMethod, "remove");
					break;
				case TypeDef type when entity is DelegateDeclaration:
					Add(entity, type.FindMethod("Invoke"), "Invoke");
					break;
				}
			}
		}

		static void Add(EntityDeclaration entity, IMemberDef? member, string? accessor) {
			if (member is null)
				return;
			var annotation = new TokenCommentAnnotation(member, accessor);
			var comment = new Comment(annotation.ToString());
			comment.AddAnnotation(annotation);
			entity.AddLeadingTrivia(comment);
		}
	}

	sealed class TokenCommentAnnotation {
		readonly IMemberDef member;
		readonly string? accessor;

		public TokenCommentAnnotation(IMemberDef member, string? accessor) {
			this.member = member;
			this.accessor = accessor;
		}

		static string ToHex(ulong value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

		public override string ToString() {
			var text = " ";
			if (accessor is not null)
				text += "(" + accessor + ") ";
			text += "Token: " + ToHex(member.MDToken.Raw) + " RID: " + member.MDToken.Rid.ToString(CultureInfo.InvariantCulture);
			if (member.GetRVA(out uint rva, out long fileOffset))
				text += " RVA: " + ToHex(rva) + " File Offset: " + ToHex((ulong)fileOffset);
			return text;
		}

		public void Write(DnSpyTextOutput output) {
			void Text(string s) => output.WriteRaw(s, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			Text("// ");
			if (accessor is not null)
				Text("(" + accessor + ") ");
			Text("Token: ");
			output.WriteRaw(ToHex(member.MDToken.Raw), new TokenReference(member), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			Text(" RID: " + member.MDToken.Rid.ToString(CultureInfo.InvariantCulture));
			if (member.GetRVA(out uint rva, out long fileOffset)) {
				var filename = member.Module?.Location;
				Text(" RVA: ");
				output.WriteRaw(ToHex(rva), new AddressReference(filename, true, rva, 0), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				Text(" File Offset: ");
				output.WriteRaw(ToHex((ulong)fileOffset), new AddressReference(filename, false, (ulong)fileOffset, 0), DecompilerReferenceFlags.None, BoxedTextColor.Comment);
			}
		}
	}
}
