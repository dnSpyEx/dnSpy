// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Composition;
using dnSpy.Roslyn.EditorFeatures.Editor;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace dnSpy.Roslyn.EditorFeatures.SmartIndent {
	[Export(typeof(ISmartIndentProvider))]
	[ContentType(ContentTypeNames.RoslynContentType)]
	class SmartIndentProvider : ISmartIndentProvider {
		public ISmartIndent CreateSmartIndent(ITextView textView) {
			if (textView == null) {
				throw new ArgumentNullException(nameof(textView));
			}

			//if (!textView.TextBuffer.GetFeatureOnOffOption(InternalFeatureOnOffOptions.SmartIndenter))
			//{
			//    return null;
			//}

			return new SmartIndent(textView);
		}
	}
}
