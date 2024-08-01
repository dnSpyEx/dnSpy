using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Media;

namespace EleCho.WpfSuite {
	/// <summary>
	/// <see cref="WindowOptionColor"/> converter
	/// </summary>
	public class WindowOptionColorConverter : TypeConverter {
		/// <inheritdoc/>
		public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) {
			if (sourceType == typeof(string) ||
				sourceType == typeof(Color))
				return true;

			return base.CanConvertFrom(context, sourceType);
		}

		/// <inheritdoc/>
		public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value) {
			if (value is string stringValue) {
				if (stringValue.Equals(nameof(WindowOptionColor.Default), StringComparison.OrdinalIgnoreCase)) {
					return WindowOptionColor.Default;
				}
				else if (stringValue.Equals(nameof(WindowOptionColor.None), StringComparison.OrdinalIgnoreCase)) {
					return WindowOptionColor.None;
				}
				else if (ColorConverter.ConvertFromString(stringValue) is Color color) {
					color.A = 0xFF;
					return (WindowOptionColor)color;
				}
			}
			else if (value is Color colorValue) {
				colorValue.A = 0xFF;
				return (WindowOptionColor)colorValue;
			}

			return base.ConvertFrom(context, culture, value);
		}
	}
}
