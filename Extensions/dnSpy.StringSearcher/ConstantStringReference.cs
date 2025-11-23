using System;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.Text;
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

		public override IMemberRef Member => Container;

		protected override FrameworkElement CreateReferrerUI() {
			var writer = WriterCache.GetWriter();

			try {
				Context.Decompiler.Write(writer, Container, DefaultFormatterOptions);

				if (Referrer is ParamDef param) {
					WriteParameterReference(writer, param);
				}

				return Context.TextElementProvider.CreateTextElement(
					Context.ClassificationFormatMap,
					new TextClassifierContext(writer.Text, string.Empty, true, writer.Colors),
					ContentTypes.Search,
					TextElementFlags.FilterOutNewLines
				);
			}
			finally {
				WriterCache.FreeWriter(writer);
			}
		}
	}
}
