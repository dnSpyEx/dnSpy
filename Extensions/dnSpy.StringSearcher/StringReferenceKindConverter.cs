using System;
using System.Globalization;
using System.Windows.Data;
using dnSpy.StringSearcher.Properties;

namespace dnSpy.StringSearcher {
	internal class StringReferenceKindConverter : IValueConverter {
		public object Convert(object value, Type targetType, object parameter, CultureInfo culture) {
			if (value is not StringReferenceKind kind) {
				throw new ArgumentOutOfRangeException(nameof(value));
			}

			return kind switch {
				StringReferenceKind.IL => dnSpy_StringSearcher_Resources.ReferenceKindIL,
				StringReferenceKind.Constant => dnSpy_StringSearcher_Resources.ReferenceKindConstant,
				StringReferenceKind.Attribute => dnSpy_StringSearcher_Resources.ReferenceKindAttribute,
				_ => throw new NotImplementedException(),
			};
		}

		public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) {
			throw new NotSupportedException();
		}
	}
}
