/*
    Copyright (C) 2025 Washi

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

using dnlib.DotNet;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;

namespace dnSpy.StringSearcher {
	public sealed class ILStringReference(StringReferenceContext context, string literal, MethodDef referrer, uint offset)
		: StringReference(context, literal, referrer) {

		public new MethodDef Referrer => (MethodDef)base.Referrer;

		public override StringReferenceKind Kind => StringReferenceKind.IL;

		public override ModuleDef Module => Referrer.Module;

		public override MDToken Token => Referrer.MDToken;

		public uint Offset { get; } = offset;

		public override object ContainerObject => Referrer;

		protected override void WriteReferrerUI(TextClassifierTextColorWriter writer) {
			Context.Decompiler.Write(writer, Referrer, DefaultFormatterOptions);
			writer.Write(TextColor.Punctuation, "+");
			writer.Write(TextColor.Label, $"IL_{Offset:X4}");
		}
	}
}
