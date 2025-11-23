using System;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;

namespace dnSpy.StringSearcher {
	public sealed class CustomAttributeStringReference(StringReferenceContext context, string literal, IMDTokenProvider owner, CustomAttribute attribute)
		: StringReference(context, literal, owner) {

		public CustomAttribute CustomAttribute { get; } = attribute;

		public override StringReferenceKind Kind => StringReferenceKind.Attribute;

		public override ModuleDef Module => Member.Module;

		public override MDToken Token => Owner.MDToken;

		public IMDTokenProvider Owner { get; } = owner;

		public override IMemberRef Member => Owner switch {
			ParamDef param => param.DeclaringMethod,
			IMemberRef reference => reference,
			_ => throw new ArgumentOutOfRangeException(nameof(Owner))
		};

		protected override FrameworkElement CreateReferrerUI() {
			var writer = WriterCache.GetWriter();

			try {
				Context.Decompiler.Write(writer, Member, DefaultFormatterOptions);

				if (Owner is ParamDef param) {
					WriteParameterReference(writer, param);
				}

				writer.Write(TextColor.Text, " in ");
				Context.Decompiler.Write(writer, CustomAttribute.AttributeType, DefaultFormatterOptions);

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
