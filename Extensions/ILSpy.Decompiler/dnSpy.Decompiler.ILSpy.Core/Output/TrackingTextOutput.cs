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
using System.Reflection.Metadata;
using System.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	enum OutputReferenceKind {
		Member,
		Type,
		MetadataHandle,
		Local,
		OpCode,
	}

	/// <summary>
	/// A reference written by the upstream decompiler. <see cref="Target"/> is an upstream object
	/// (<see cref="IMember"/>, <see cref="IType"/>, <see cref="MetadataHandleTarget"/>, a local/parameter object, or an <see cref="OpCodeInfo"/>).
	/// </summary>
	readonly struct OutputReference {
		public int Start { get; }
		public int Length { get; }
		public object Target { get; }
		public OutputReferenceKind Kind { get; }
		public bool IsDefinition { get; }

		public OutputReference(int start, int length, object target, OutputReferenceKind kind, bool isDefinition) {
			Start = start;
			Length = length;
			Target = target;
			Kind = kind;
			IsDefinition = isDefinition;
		}
	}

	sealed class MetadataHandleTarget {
		public MetadataFile File { get; }
		public Handle Handle { get; }
		public MetadataHandleTarget(MetadataFile file, Handle handle) {
			File = file;
			Handle = handle;
		}
	}

	/// <summary>
	/// Converts the text locations the upstream decompiler stores in the syntax tree to offsets in the output
	/// </summary>
	interface ILocationMapper {
		/// <summary>
		/// Converts a (1-based) location to an offset in the output
		/// </summary>
		int ToOffset(int line, int column);
	}

	/// <summary>
	/// <see cref="ITextOutput"/> that records references and knows the current line/column so the upstream
	/// decompiler can store text locations in the syntax tree (needed to create sequence points).
	/// </summary>
	sealed class TrackingTextOutput : ITextOutput, ILocationMapper {
		readonly StringBuilder sb = new StringBuilder();
		readonly List<int> lineStarts = new List<int> { 0 };
		readonly List<OutputReference> references = new List<OutputReference>();
		int indent;
		bool needsIndent = true;

		public string IndentationString { get; set; } = "\t";
		public IReadOnlyList<OutputReference> References => references;
		public int Length => sb.Length;

		/// <summary>
		/// Location (1-based) where the next character will be written. Pending indentation is included.
		/// </summary>
		public TextLocation Location {
			get {
				int column = sb.Length - lineStarts[lineStarts.Count - 1] + 1;
				if (needsIndent)
					column += indent * IndentationString.Length;
				return new TextLocation(lineStarts.Count, column);
			}
		}

		/// <summary>
		/// Converts a (1-based) location to an offset in the text
		/// </summary>
		public int ToOffset(int line, int column) {
			if (line < 1)
				return 0;
			if (line > lineStarts.Count)
				return sb.Length;
			int offset = lineStarts[line - 1] + Math.Max(0, column - 1);
			return Math.Min(offset, sb.Length);
		}

		public override string ToString() => sb.ToString();

		public void Indent() => indent++;
		public void Unindent() => indent--;

		void WriteIndent() {
			if (!needsIndent)
				return;
			needsIndent = false;
			for (int i = 0; i < indent; i++)
				sb.Append(IndentationString);
		}

		public void Write(char ch) {
			WriteIndent();
			Append(ch);
		}

		public void Write(string text) {
			WriteIndent();
			foreach (var c in text)
				Append(c);
		}

		void Append(char c) {
			sb.Append(c);
			if (c == '\n')
				lineStarts.Add(sb.Length);
		}

		public void WriteLine() {
			Append('\n');
			needsIndent = true;
		}

		void WriteReference(string text, object target, OutputReferenceKind kind, bool isDefinition) {
			WriteIndent();
			int start = sb.Length;
			Write(text);
			references.Add(new OutputReference(start, sb.Length - start, target, kind, isDefinition));
		}

		public void WriteReference(OpCodeInfo opCode, bool omitSuffix = false) =>
			WriteReference(omitSuffix ? opCode.Name.TrimEnd('.') : opCode.Name, opCode, OutputReferenceKind.OpCode, false);

		public void WriteReference(MetadataFile metadata, Handle handle, string text, string protocol = "decompile", bool isDefinition = false) =>
			WriteReference(text, new MetadataHandleTarget(metadata, handle), OutputReferenceKind.MetadataHandle, isDefinition);

		public void WriteReference(IType type, string text, bool isDefinition = false) =>
			WriteReference(text, type, OutputReferenceKind.Type, isDefinition);

		public void WriteReference(IMember member, string text, bool isDefinition = false) =>
			WriteReference(text, member, OutputReferenceKind.Member, isDefinition);

		public void WriteLocalReference(string text, object reference, bool isDefinition = false, bool isHoverOnly = false) =>
			WriteReference(text, reference, OutputReferenceKind.Local, isDefinition);

		public void MarkFoldStart(string collapsedText = "...", bool defaultCollapsed = false, bool isDefinition = false) { }
		public void MarkDefinitionStart() { }
		public void MarkFoldEnd() { }
	}

	/// <summary>
	/// Upstream <see cref="TextTokenWriter"/> (which decides what each identifier refers to) that also reports
	/// its location, so it can be wrapped with <see cref="TokenWriter.WrapInWriterThatSetsLocationsInAST(TokenWriter)"/>
	/// </summary>
	sealed class LocatableTextTokenWriter : TextTokenWriter, ILocatable {
		readonly TrackingTextOutput output;

		public LocatableTextTokenWriter(TrackingTextOutput output, DecompilerSettings settings) : base(output, settings) =>
			this.output = output;

		public TextLocation Location => output.Location;
		public int Length => output.Length;
	}
}
