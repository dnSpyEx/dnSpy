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

		public FrameworkElement LiteralUI => literalUI ??= CreateLiteralUI();

		public FrameworkElement ModuleUI => moduleUI ??= CreateModuleUI();

		public FrameworkElement ReferrerUI => referrerUI ??= CreateReferrerUI();

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
}
