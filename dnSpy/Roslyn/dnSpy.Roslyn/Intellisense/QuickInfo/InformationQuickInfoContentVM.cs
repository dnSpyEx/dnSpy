/*
    Copyright (C) 2014-2019 de4dot@gmail.com

    This file is part of dnSpy

    dnSpy is free software: you can redistribute it and/or modify
    it under the terms of the GNU General Public License as published by
    the Free Software Foundation, either version 3 of the License, or
    (at your option) any later version.

    dnSpy is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with dnSpy.  If not, see <http://www.gnu.org/licenses/>.
*/

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text;
using System.Windows;
using dnSpy.Contracts.Images;
using dnSpy.Contracts.MVVM;
using dnSpy.Contracts.Text.Classification;
using dnSpy.Roslyn.Glyphs;
using dnSpy.Roslyn.Internal.QuickInfo;
using dnSpy.Roslyn.Text.Classification;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Text.Editor;

namespace dnSpy.Roslyn.Intellisense.QuickInfo {
	sealed class InformationQuickInfoContentVM : ViewModelBase {
		public ImageReference SymbolImageReference { get; }
		public bool HasSymbolImageReference => !SymbolImageReference.IsDefault;
		public ImageReference WarningImageReference { get; }
		public bool HasWarningImageReference => !WarningImageReference.IsDefault;
		public object? MainDescriptionObject { get; }
		public object? DocumentationObject { get; }
		public bool HasDocumentationObject => DocumentationObject is not null;
		public object? UsageObject { get; }
		public bool HasUsageObject => UsageObject is not null;
		public object? TypeParameterMapObject { get; }
		public bool HasTypeParameterMapObject => TypeParameterMapObject is not null;
		public object? AnonymousTypesObject { get; }
		public bool HasAnonymousTypesObject => AnonymousTypesObject is not null;
		public object? ExceptionObject { get; }
		public bool HasExceptionObject => ExceptionObject is not null;

		public InformationQuickInfoContentVM(ITextView textView, InformationQuickInfoContent content, IClassificationFormatMap classificationFormatMap, IThemeClassificationTypeService themeClassificationTypeService, ITextElementFactory textElementFactory) {
			if (textView is null)
				throw new ArgumentNullException(nameof(textView));
			if (content is null)
				throw new ArgumentNullException(nameof(content));
			if (classificationFormatMap is null)
				throw new ArgumentNullException(nameof(classificationFormatMap));
			if (themeClassificationTypeService is null)
				throw new ArgumentNullException(nameof(themeClassificationTypeService));
			var sb = new StringBuilder();
			if (content.SymbolGlyph is not null)
				SymbolImageReference = content.SymbolGlyph.Value.GetImageReference() ?? default;
			if (content.WarningGlyph is not null)
				WarningImageReference = content.WarningGlyph.Value.GetImageReference() ?? default;
			MainDescriptionObject = TryCreateObject(sb, content.MainDescription, classificationFormatMap, themeClassificationTypeService, textElementFactory);
			DocumentationObject = TryCreateObject(sb, content.Documentation, classificationFormatMap, themeClassificationTypeService, textElementFactory);
			UsageObject = TryCreateObject(sb, content.UsageText, classificationFormatMap, themeClassificationTypeService, textElementFactory);
			TypeParameterMapObject = TryCreateObject(sb, content.TypeParameterMap, classificationFormatMap, themeClassificationTypeService, textElementFactory);
			AnonymousTypesObject = TryCreateObject(sb, content.AnonymousTypes, classificationFormatMap, themeClassificationTypeService, textElementFactory);
			ExceptionObject = TryCreateObject(sb, content.ExceptionText, classificationFormatMap, themeClassificationTypeService, textElementFactory);
		}

		static FrameworkElement? TryCreateObject(StringBuilder sb, ImmutableArray<TaggedText> taggedParts, IClassificationFormatMap classificationFormatMap, IThemeClassificationTypeService themeClassificationTypeService, ITextElementFactory textElementFactory) {
			if (taggedParts.IsDefaultOrEmpty)
				return null;
			var text = ToString(sb, taggedParts);
			return textElementFactory.Create(classificationFormatMap, text, CreateTextClassificationTags(taggedParts, themeClassificationTypeService), TextElementFlags.None);
		}

		static IList<TextClassificationTag> CreateTextClassificationTags(ImmutableArray<TaggedText> taggedParts, IThemeClassificationTypeService themeClassificationTypeService) {
			int pos = 0;
			var result = new TextClassificationTag[taggedParts.Length];
			for (int i = 0; i < taggedParts.Length; i++) {
				var part = taggedParts[i];
				var color = TextTagsHelper.ToTextColor(part.Tag);
				var classificationType = themeClassificationTypeService.GetClassificationType(color);
				result[i] = new TextClassificationTag(new Span(pos, part.Text.Length), classificationType);
				pos += part.Text.Length;
			}
			return result;
		}

		static string ToString(StringBuilder sb, ImmutableArray<TaggedText> taggedParts) {
			sb.Clear();
			foreach (var part in taggedParts)
				sb.Append(part.Text);
			return sb.ToString();
		}
	}
}
