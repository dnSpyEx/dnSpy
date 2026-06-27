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
using System.Collections.Immutable;
using System.Windows;
using dnSpy.Contracts.Text.Classification;
using Microsoft.CodeAnalysis;
using Microsoft.VisualStudio.Text.Classification;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Roslyn.Text.Classification {
	sealed class TaggedTextElementProvider : ITaggedTextElementProvider {
		readonly IContentType contentType;
		readonly IClassificationFormatMap classificationFormatMap;
		readonly ITextElementProvider textElementProvider;

		public TaggedTextElementProvider(IContentType contentType, IClassificationFormatMap classificationFormatMap, ITextElementProvider textElementProvider) {
			this.contentType = contentType ?? throw new ArgumentNullException(nameof(contentType));
			this.classificationFormatMap = classificationFormatMap ?? throw new ArgumentNullException(nameof(classificationFormatMap));
			this.textElementProvider = textElementProvider ?? throw new ArgumentNullException(nameof(textElementProvider));
		}

		public FrameworkElement Create(string tag, ImmutableArray<TaggedText> taggedParts, bool colorize) {
			var context = TaggedTextClassifierContext.Create(tag, taggedParts, colorize);
			return textElementProvider.CreateTextElement(classificationFormatMap, context, contentType, TextElementFlags.None);
		}

		public void Dispose() { }
	}
}
