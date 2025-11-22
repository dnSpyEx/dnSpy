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
		MethodBodyString,
		Constant
	}

	public abstract class StringReference(StringReferenceContext context, string literal, IMDTokenProvider referrer) : ViewModelBase {
		protected const FormatterOptions DefaultFormatterOptions = FormatterOptions.Default & ~(
			FormatterOptions.ShowParameterNames
			| FormatterOptions.ShowFieldLiteralValues
			| FormatterOptions.ShowReturnTypes
		);

		private string? formatted;
		private bool isVerbatim;
		private FrameworkElement? literalUI;
		private FrameworkElement? moduleUI;
		private FrameworkElement? referrerUI;

		public StringReferenceContext Context { get; } = context;

		public string Literal { get; } = literal;

		public IMDTokenProvider Referrer { get; } = referrer;

		public abstract StringReferenceKind Kind { get; }

		public abstract ModuleDef Module { get; }

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

		protected static class WriterCache {
			static readonly TextClassifierTextColorWriter writer = new();
			public static TextClassifierTextColorWriter GetWriter() => writer;
			public static void FreeWriter(TextClassifierTextColorWriter writer) => writer.Clear();
		}
	}

	public sealed class MethodBodyStringReference(StringReferenceContext context, string literal, MethodDef referrer, uint offset)
		: StringReference(context, literal, referrer) {

		public new MethodDef Referrer => (MethodDef)base.Referrer;

		public override StringReferenceKind Kind => StringReferenceKind.MethodBodyString;

		public override ModuleDef Module => Referrer.Module;

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

		protected override FrameworkElement CreateReferrerUI() {
			var writer = WriterCache.GetWriter();

			try {
				Context.Decompiler.Write(writer, Container, DefaultFormatterOptions);

				if (Referrer is ParamDef param) {
					writer.Write(TextColor.Punctuation, "@");
					writer.Write(TextColor.Parameter, param.Name);
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
