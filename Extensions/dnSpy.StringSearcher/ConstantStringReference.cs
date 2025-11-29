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

using System;
using dnlib.DotNet;
using dnSpy.Contracts.Text.Classification;

namespace dnSpy.StringSearcher {
	public sealed class ConstantStringReference(StringReferenceContext context, string literal, IHasConstant referrer)
		: StringReference(context, literal, referrer) {

		public new IHasConstant Referrer => (IHasConstant)base.Referrer;

		public IMemberDef Container => Referrer switch {
			FieldDef or PropertyDef => (IMemberDef)Referrer,
			ParamDef param => param.DeclaringMethod,
			_ => throw new ArgumentOutOfRangeException(nameof(Referrer)),
		};

		public override StringReferenceKind Kind => StringReferenceKind.Constant;

		public override ModuleDef Module => Container.Module;

		public override MDToken Token => Referrer.MDToken;

		public override object ContainerObject => Container;

		protected override void WriteReferrerUI(TextClassifierTextColorWriter writer) {
			Context.Decompiler.Write(writer, Container, DefaultFormatterOptions);

			if (Referrer is ParamDef param) {
				WriteParameterReference(writer, param);
			}
		}
	}
}
