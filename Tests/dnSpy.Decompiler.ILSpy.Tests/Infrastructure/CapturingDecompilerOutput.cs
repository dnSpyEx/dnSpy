/*
    Copyright (C) 2026 dnSpy contributors

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
using System.Linq;
using System.Text;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	readonly struct WrittenSpan {
		public int Start { get; }
		public string Text { get; }
		public object Color { get; }
		public object? Reference { get; }
		public DecompilerReferenceFlags Flags { get; }
		public int End => Start + Text.Length;

		public WrittenSpan(int start, string text, object color, object? reference, DecompilerReferenceFlags flags) {
			Start = start;
			Text = text;
			Color = color;
			Reference = reference;
			Flags = flags;
		}

		public override string ToString() => $"{Start}: '{Text}' {Color} {Reference} {Flags}";
	}

	/// <summary>
	/// Records everything a decompiler writes, like dnSpy's document viewer output (same indentation behavior)
	/// </summary>
	sealed class CapturingDecompilerOutput : IDecompilerOutput {
		readonly StringBuilder sb = new StringBuilder();
		readonly Indenter indenter = new Indenter(4, 4, true);
		bool addIndent = true;

		public List<WrittenSpan> Spans { get; } = new List<WrittenSpan>();
		public List<(string id, object? data)> CustomData { get; } = new List<(string, object?)>();
		public bool UsesCustomData { get; set; } = true;

		public int Length => sb.Length;
		public int NextPosition => sb.Length + (addIndent ? indenter.String.Length : 0);
		public string Text => sb.ToString();
		public override string ToString() => sb.ToString();

		public IEnumerable<MethodDebugInfo> DebugInfos => CustomData.Where(a => a.id == PredefinedCustomDataIds.DebugInfo).Select(a => (MethodDebugInfo)a.data!);
		public IEnumerable<CodeBracesRange> BracePairs => CustomData.Where(a => a.id == PredefinedCustomDataIds.CodeBracesRange).Select(a => (CodeBracesRange)a.data!);
		public IEnumerable<int> LineSeparators => CustomData.Where(a => a.id == PredefinedCustomDataIds.LineSeparator).Select(a => ((LineSeparator)a.data!).Position);
		public IEnumerable<WrittenSpan> References => Spans.Where(a => a.Reference is not null);

		public void IncreaseIndent() => indenter.IncreaseIndent();
		public void DecreaseIndent() => indenter.DecreaseIndent();

		public void WriteLine() {
			sb.Append(Environment.NewLine);
			addIndent = true;
		}

		void Add(string text, object color, object? reference, DecompilerReferenceFlags flags) {
			if (addIndent) {
				addIndent = false;
				sb.Append(indenter.String);
			}
			Spans.Add(new WrittenSpan(sb.Length, text, color, reference, flags));
			sb.Append(text);
		}

		public void Write(string text, object color) => Add(text, color, null, DecompilerReferenceFlags.None);
		public void Write(string text, int index, int length, object color) => Add(text.Substring(index, length), color, null, DecompilerReferenceFlags.None);
		public void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) => Add(text, color, reference, flags);
		public void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) =>
			Add(text.Substring(index, length), color, reference, flags);
		public void AddCustomData<TData>(string id, TData data) => CustomData.Add((id, data));

		/// <summary>
		/// Gets the spans written at a position
		/// </summary>
		public IEnumerable<WrittenSpan> SpansAt(int position) => Spans.Where(a => a.Start <= position && position < a.End);
	}
}
