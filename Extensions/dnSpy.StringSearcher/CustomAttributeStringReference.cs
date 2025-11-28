using dnlib.DotNet;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;

namespace dnSpy.StringSearcher {
	public sealed class CustomAttributeStringReference(
		StringReferenceContext context,
		string literal,
		IHasCustomAttribute referrer,
		IMDTokenProvider container,
		ModuleDef module,
		CustomAttribute attribute,
		string argumentName)
		: StringReference(context, literal, referrer) {

		public CustomAttribute CustomAttribute { get; } = attribute;

		public override StringReferenceKind Kind => StringReferenceKind.Attribute;

		public override ModuleDef Module => module;

		public override MDToken Token => ((IMDTokenProvider)ContainerObject).MDToken;

		public override object ContainerObject => container;

		protected override void WriteReferrerUI(TextClassifierTextColorWriter writer) {
			switch (ContainerObject) {
			case ModuleDef module:
				writer.WriteModule(module.Name);
				break;
			case AssemblyDef assembly:
				new NodeFormatter().Write(writer, Context.Decompiler, assembly, false, true, true);
				break;
			case IMemberRef memberRef:
				Context.Decompiler.Write(writer, memberRef, DefaultFormatterOptions);
				break;
			default:
				writer.Write(ContainerObject.ToString()!);
				break;
			}

			switch (Referrer) {
			case ParamDef param:
				WriteParameterReference(writer, param);
				break;
			case GenericParam param:
				writer.Write(TextColor.DarkGray, " generic ");
				Context.Decompiler.Write(writer, param, DefaultFormatterOptions);
				break;
			}

			writer.Write(TextColor.DarkGray, " in ");
			Context.Decompiler.Write(writer, CustomAttribute.AttributeType, DefaultFormatterOptions);
			writer.Write(TextColor.Punctuation, " (");
			writer.Write(TextColor.InstanceProperty, argumentName);
			writer.Write(TextColor.Punctuation, ")");
		}
	}
}
