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
