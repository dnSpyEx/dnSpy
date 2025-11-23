using System;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using Microsoft.VisualStudio.Text.Classification;

namespace dnSpy.StringSearcher {
	public record StringReferenceContext(
		IDecompiler Decompiler,
		ITextElementProvider TextElementProvider,
		IClassificationFormatMap ClassificationFormatMap,
		IDotNetImageService DotNetImageService
	);

	public enum StringReferenceKind {
		IL,
		Constant,
		Attribute
	}

	public abstract class StringReference(StringReferenceContext context, string literal, object referrer) : ViewModelBase {
		protected const FormatterOptions DefaultFormatterOptions = FormatterOptions.Default & ~(
			FormatterOptions.ShowParameterNames
			| FormatterOptions.ShowFieldLiteralValues
			| FormatterOptions.ShowReturnTypes
			| FormatterOptions.ShowFieldTypes
		);

		private string? formatted;
		private bool isVerbatim;
		private FrameworkElement? literalUI;
		private FrameworkElement? moduleUI;
		private FrameworkElement? referrerUI;

		public StringReferenceContext Context { get; } = context;

		public string Literal { get; } = literal;

		public object Referrer { get; } = referrer;

		public abstract StringReferenceKind Kind { get; }

		public abstract ModuleDef Module { get; }

		public abstract IMemberRef Member { get; }

		public abstract MDToken Token { get; }

		public string FormattedLiteral => formatted ??= StringFormatter.ToFormattedString(Literal, out isVerbatim);

		public FrameworkElement? LiteralUI => literalUI ??= CreateLiteralUI();

		public FrameworkElement? ModuleUI => moduleUI ??= CreateModuleUI();

		public FrameworkElement? ReferrerUI => referrerUI ??= CreateReferrerUI();

		public ImageReference ReferrerImage => Member switch {
			MethodDef method => Context.DotNetImageService.GetImageReference(method),
			FieldDef field => Context.DotNetImageService.GetImageReference(field),
			PropertyDef property => Context.DotNetImageService.GetImageReference(property),
			TypeDef type => Context.DotNetImageService.GetImageReference(type),
			_ => Context.DotNetImageService.GetNamespaceImageReference()
		};

		private FrameworkElement CreateLiteralUI() {
			var writer = WriterCache.GetWriter();

			try {
				var s = FormattedLiteral;
				writer.Write(isVerbatim ? BoxedTextColor.VerbatimString : BoxedTextColor.String, s);

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

		private FrameworkElement CreateModuleUI() {
			var writer = WriterCache.GetWriter();

			try {
				writer.WriteModule(Module.Name);

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

		protected abstract FrameworkElement CreateReferrerUI();

		protected static void WriteParameterReference(TextClassifierTextColorWriter writer, ParamDef param) {
			writer.Write(TextColor.Punctuation, "@");
			if (param.DeclaringMethod.Parameters.ReturnParameter.ParamDef == param) {
				writer.Write(TextColor.Keyword, "return");
			}
			else {
				writer.Write(TextColor.Parameter, param.Name);
			}
		}

		protected static class WriterCache {
			static readonly TextClassifierTextColorWriter writer = new();
			public static TextClassifierTextColorWriter GetWriter() => writer;
			public static void FreeWriter(TextClassifierTextColorWriter writer) => writer.Clear();
		}
	}

	public sealed class ILStringReference(StringReferenceContext context, string literal, MethodDef referrer, uint offset)
		: StringReference(context, literal, referrer) {

		public new MethodDef Referrer => (MethodDef)base.Referrer;

		public override StringReferenceKind Kind => StringReferenceKind.IL;

		public override ModuleDef Module => Referrer.Module;

		public override MDToken Token => Referrer.MDToken;

		public uint Offset { get; } = offset;

		public override IMemberRef Member => Referrer;

		protected override FrameworkElement CreateReferrerUI() {
			var writer = WriterCache.GetWriter();

			try {
				Context.Decompiler.Write(writer, Referrer, DefaultFormatterOptions);
				writer.Write(TextColor.Punctuation, "+");
				writer.Write(TextColor.Label, $"IL_{Offset:X4}");

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
