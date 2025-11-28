using System.Diagnostics.CodeAnalysis;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using dnSpy.StringSearcher.Properties;
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

		private bool isVerbatim;
		private ImageReference? referrerImage;
		private FrameworkElement? referrerUI;
		private string? referrerString;

		public StringReferenceContext Context { get; } = context;

		public string Literal { get; } = literal;

		public object Referrer { get; } = referrer;

		public abstract StringReferenceKind Kind { get; }

		public abstract ModuleDef Module { get; }

		public abstract object ContainerObject { get; }

		public abstract MDToken Token { get; }

		public string FormattedLiteral => field ??= StringFormatter.ToFormattedString(Literal, out isVerbatim);

		public FrameworkElement LiteralUI => field ??= CreateLiteralUI();

		public FrameworkElement ModuleUI => field ??= CreateModuleUI();

		public FrameworkElement ReferrerUI {
			get {
				EnsureReferrerInitialized();
				return referrerUI;
			}
		}

		public string ReferrerString {
			get {
				EnsureReferrerInitialized();
				return referrerString;
			}
		}

		public ImageReference ReferrerImage => referrerImage ??= ContainerObject switch {
			AssemblyDef assembly => Context.DotNetImageService.GetImageReference(assembly),
			ModuleDef module => Context.DotNetImageService.GetImageReference(module),
			TypeDef type => Context.DotNetImageService.GetImageReference(type),
			MethodDef method => Context.DotNetImageService.GetImageReference(method),
			FieldDef @field => Context.DotNetImageService.GetImageReference(@field),
			PropertyDef property => Context.DotNetImageService.GetImageReference(property),
			EventDef @event => Context.DotNetImageService.GetImageReference(@event),
			Parameter => Context.DotNetImageService.GetImageReferenceParameter(),
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

		[MemberNotNull(nameof(referrerUI), nameof(referrerString))]
		private void EnsureReferrerInitialized() {
			if (referrerUI is not null && referrerString is not null) {
				return;
			}

			var writer = WriterCache.GetWriter();
			try {
				WriteReferrerUI(writer);

				referrerString = writer.Text;
				referrerUI = Context.TextElementProvider.CreateTextElement(
					Context.ClassificationFormatMap,
					new TextClassifierContext(referrerString, string.Empty, true, writer.Colors),
					ContentTypes.Search,
					TextElementFlags.FilterOutNewLines
				);
			}
			finally {
				WriterCache.FreeWriter(writer);
			}
		}

		protected abstract void WriteReferrerUI(TextClassifierTextColorWriter writer);

		protected static void WriteParameterReference(TextClassifierTextColorWriter writer, ParamDef param) {
			writer.Write(" ");
			writer.Write(TextColor.DarkGray, dnSpy_StringSearcher_Resources.ReferrerParameter);
			writer.Write(" ");

			if (param.DeclaringMethod.Parameters.ReturnParameter.ParamDef == param) {
				writer.Write(TextColor.Keyword, "return");
			}
			else {
				writer.Write(TextColor.Parameter, param.Name);
			}
		}

		private static class WriterCache {
			static readonly TextClassifierTextColorWriter writer = new();
			public static TextClassifierTextColorWriter GetWriter() => writer;
			public static void FreeWriter(TextClassifierTextColorWriter writer) => writer.Clear();
		}
	}
}
