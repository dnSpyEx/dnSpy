using System;
using System.Windows;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Text;
using dnSpy.Contracts.Text.Classification;
using Microsoft.VisualStudio.Text.Classification;

namespace dnSpy.StringSearcher {
	public record StringReferenceContext(
		IDecompiler Decompiler,
		ITextElementProvider TextElementProvider,
		IClassificationFormatMap ClassificationFormatMap
	);

	public enum StringReferenceKind {
		MethodBodyString
	}

	public abstract class StringReference(StringReferenceContext context, string literal, IMDTokenProvider referrer) : ViewModelBase {
		private string? formatted;
		private bool isVerbatim;
		private FrameworkElement? literalUI;
		private FrameworkElement? moduleUI;
		private FrameworkElement? referrerUI;

		public StringReferenceContext Context { get; } = context;

		public string Literal { get; } = literal;

		public IMDTokenProvider Referrer { get; } = referrer;

		public abstract StringReferenceKind Kind { get; }

		public ModuleDef Module => ((IOwnerModule)Referrer).Module;

		public string FormattedLiteral => formatted ??= StringFormatter.ToFormattedString(Literal, out isVerbatim);

		public FrameworkElement? LiteralUI => literalUI ??= CreateLiteralUI();

		public FrameworkElement? ModuleUI => moduleUI ??= CreateModuleUI();

		public FrameworkElement? ReferrerUI => referrerUI ??= CreateReferrerUI();

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
			if (Referrer is not IOwnerModule { Module: { } module }) {
				throw new InvalidOperationException();
			}
			
			var writer = WriterCache.GetWriter();

			try {
				writer.WriteModule(module.Name);

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

		protected static class WriterCache {
			static readonly TextClassifierTextColorWriter writer = new();
			public static TextClassifierTextColorWriter GetWriter() => writer;
			public static void FreeWriter(TextClassifierTextColorWriter writer) => writer.Clear();
		}
	}

	public sealed class MethodBodyStringReference(StringReferenceContext context, string literal, MethodDef referrer, uint offset)
		: StringReference(context, literal, referrer) {

		private const FormatterOptions DefaultFormatterOptions = FormatterOptions.Default & ~(
			FormatterOptions.ShowParameterNames
			| FormatterOptions.ShowFieldLiteralValues
			| FormatterOptions.ShowReturnTypes
		);

		public new MethodDef Referrer => (MethodDef)base.Referrer;

		public override StringReferenceKind Kind => StringReferenceKind.MethodBodyString;

		public uint Offset { get; } = offset;

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
}
