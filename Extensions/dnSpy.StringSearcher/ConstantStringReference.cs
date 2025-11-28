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
