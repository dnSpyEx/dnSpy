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
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Disassembler;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// The upstream decompiler's <see cref="ITextOutput"/> writing to dnSpy's <see cref="IDecompilerOutput"/>:
	/// converts references to dnlib objects, colors the text (the token writer tells it how) and keeps track of
	/// lines so the text locations upstream stores in the syntax tree can be converted to output positions.
	/// </summary>
	sealed class DnSpyTextOutput : ITextOutput, ILocationMapper {
		readonly IDecompilerOutput output;
		readonly ReferenceFactory references;
		readonly SourceVariableTable variables;
		// Positions in 'output' where each line starts. A line start is where the line's indentation (if any) starts.
		readonly List<int> lineStarts = new List<int>();
		// Upstream computes an identifier's end location from its name, but the written name can be longer
		// ('@', escaped characters) or shorter (truncated). Maps the computed location (and the few columns
		// after it that upstream derives from it, eg. "catch (Exception ex)" ends one column after the name)
		// to the real position, but only if the computed position can't be a real one (inside the written
		// identifier or past the end of the line). Key: line.
		Dictionary<int, List<(int computedColumn, int realStart, int realEnd)>>? identifierEnds;
		const int MaxColumnsAfterIdentifier = 3;
		StringBuilder? buffer;
		object? bufferColor;
		int bufferStart;
		bool pendingAt;

		/// <summary>
		/// Color of text that isn't a reference
		/// </summary>
		public object TextColor { get; set; } = BoxedTextColor.Text;

		/// <summary>
		/// If not null, the color of everything that's written, including references (keywords, operators)
		/// </summary>
		public object? ForcedColor { get; set; }

		/// <summary>
		/// True while an identifier is written: it's escaped the same way dnSpy's other decompilers escape
		/// identifiers (invalid characters, very long names)
		/// </summary>
		public bool IsIdentifier { get; set; }

		/// <summary>
		/// Reference used if an identifier is written without one (eg. namespaces)
		/// </summary>
		public object? IdentifierReference { get; set; }

		/// <summary>
		/// Flags of <see cref="IdentifierReference"/>
		/// </summary>
		public DecompilerReferenceFlags IdentifierReferenceFlags { get; set; }

		public IDecompilerOutput Output => output;
		public ReferenceFactory References => references;

		/// <summary>
		/// Position where the next character will be written
		/// </summary>
		public int NextPosition => buffer is null ? output.NextPosition : bufferStart + buffer.Length;

		public DnSpyTextOutput(IDecompilerOutput output, ReferenceFactory references, SourceVariableTable variables) {
			this.output = output ?? throw new ArgumentNullException(nameof(output));
			this.references = references ?? throw new ArgumentNullException(nameof(references));
			this.variables = variables ?? throw new ArgumentNullException(nameof(variables));
			lineStarts.Add(output.Length);
		}

		/// <summary>
		/// Location (1-based) where the next character will be written. Pending indentation is included.
		/// Lines are counted from where this instance was created.
		/// </summary>
		public TextLocation Location => new TextLocation(lineStarts.Count, NextPosition - lineStarts[lineStarts.Count - 1] + 1);

		public int ToOffset(int line, int column) {
			if (identifierEnds is not null && identifierEnds.TryGetValue(line, out var ends)) {
				int position = line >= 1 && line <= lineStarts.Count ? lineStarts[line - 1] + Math.Max(0, column - 1) : -1;
				int nextLineStart = line < lineStarts.Count ? lineStarts[line] : int.MaxValue;
				int endOfText = output.Length;
				foreach (var (computedColumn, realStart, realEnd) in ends) {
					if (column < computedColumn || column > computedColumn + MaxColumnsAfterIdentifier)
						continue;
					bool insideIdentifier = realStart < position && position < realEnd;
					bool pastEndOfLine = position >= nextLineStart || position > endOfText;
					if (insideIdentifier || pastEndOfLine)
						return realEnd + (column - computedColumn);
				}
			}
			if (line < 1)
				return lineStarts[0];
			if (line > lineStarts.Count)
				return output.NextPosition;
			return lineStarts[line - 1] + Math.Max(0, column - 1);
		}

		/// <summary>
		/// Called after an identifier was written at <paramref name="start"/>: upstream's computed end location of
		/// the identifier is <paramref name="computedEnd"/>, the real end is the current position
		/// </summary>
		public void SetIdentifierEnd(TextLocation computedEnd, int start) {
			if (computedEnd.IsEmpty || computedEnd.Line != lineStarts.Count)
				return;
			int end = NextPosition;
			if (lineStarts[computedEnd.Line - 1] + computedEnd.Column - 1 == end)
				return;
			identifierEnds ??= new Dictionary<int, List<(int, int, int)>>();
			if (!identifierEnds.TryGetValue(computedEnd.Line, out var ends))
				identifierEnds.Add(computedEnd.Line, ends = new List<(int, int, int)>());
			ends.Add((computedEnd.Column, start, end));
		}

		// Upstream sets it to the formatting options' indentation; dnSpy's output does its own indentation
		public string IndentationString { get; set; } = "\t";
		public void Indent() => output.IncreaseIndent();
		public void Unindent() => output.DecreaseIndent();

		/// <summary>
		/// Writes everything until <see cref="EndBuffer"/> as one piece of text (literals are written one
		/// character at a time by upstream)
		/// </summary>
		public void BeginBuffer(object color) {
			FlushBuffer();
			bufferStart = output.NextPosition;
			buffer = new StringBuilder();
			bufferColor = color;
		}

		/// <summary>
		/// Writes the buffered text and returns it and where it was written
		/// </summary>
		public (string text, int start) EndBuffer() {
			var result = (buffer?.ToString() ?? string.Empty, bufferStart);
			FlushBuffer();
			return result;
		}

		void FlushBuffer() {
			if (buffer is null)
				return;
			var text = buffer.ToString();
			var color = bufferColor ?? BoxedTextColor.Text;
			buffer = null;
			bufferColor = null;
			if (text.Length != 0)
				WriteCore(text, null, DecompilerReferenceFlags.None, color);
		}

		public void Write(char ch) {
			if (ch == '@' && IsIdentifier && buffer is null) {
				// Verbatim identifier: '@' is part of the identifier's reference
				pendingAt = true;
				return;
			}
			Write(ch.ToString());
		}

		public void Write(string text) {
			if (buffer is not null) {
				buffer.Append(text);
				return;
			}
			if (IsIdentifier && IdentifierReference is not null)
				WriteText(text, IdentifierReference, IdentifierReferenceFlags, ForcedColor ?? TextColor);
			else
				WriteText(text, null, DecompilerReferenceFlags.None, ForcedColor ?? TextColor);
		}

		public void WriteLine() {
			FlushBuffer();
			FlushPendingAt();
			output.WriteLine();
			lineStarts.Add(output.Length);
		}

		void FlushPendingAt() {
			if (pendingAt) {
				pendingAt = false;
				WriteCore("@", null, DecompilerReferenceFlags.None, ForcedColor ?? TextColor);
			}
		}

		void WriteText(string? text, object? reference, DecompilerReferenceFlags flags, object color) {
			FlushBuffer();
			// Upstream can write a null name (e.g. stack slots in the typed IL view): nothing is written, like upstream's outputs
			text ??= string.Empty;
			if (IsIdentifier) {
				text = IdentifierEscaper.Escape(text);
				if (pendingAt) {
					pendingAt = false;
					text = "@" + text;
				}
			}
			else
				FlushPendingAt();
			WriteCore(text, reference, flags, color);
		}

		void WriteCore(string text, object? reference, DecompilerReferenceFlags flags, object color) {
			if (text.Length == 0)
				return;
			int start = output.NextPosition;
			if (reference is null)
				output.Write(text, color);
			else
				output.Write(text, reference, flags, color);
			// Literals and comments can contain new lines
			for (int i = text.IndexOf('\n'); i >= 0; i = text.IndexOf('\n', i + 1))
				lineStarts.Add(start + i + 1);
		}

		public void WriteReference(OpCodeInfo opCode, bool omitSuffix = false) =>
			Write(omitSuffix ? opCode.Name.TrimEnd('.') : opCode.Name);

		public void WriteReference(MetadataFile metadata, Handle handle, string text, string protocol = "decompile", bool isDefinition = false) {
			object? reference = null;
			if (!handle.IsNil && IsEntityHandle(handle.Kind))
				reference = references.Cache.ToDnlib(metadata, (EntityHandle)handle);
			var flags = isDefinition ? DecompilerReferenceFlags.Definition : DecompilerReferenceFlags.None;
			WriteText(text, reference, flags, ForcedColor ?? references.GetColor(reference, null));
		}

		static bool IsEntityHandle(HandleKind kind) =>
			kind switch {
				HandleKind.TypeDefinition or HandleKind.FieldDefinition or HandleKind.MethodDefinition or HandleKind.PropertyDefinition or
				HandleKind.EventDefinition or HandleKind.Parameter or HandleKind.GenericParameter => true,
				_ => false,
			};

		public void WriteReference(IType type, string text, bool isDefinition = false) {
			var reference = references.GetTypeReference(type);
			var flags = isDefinition ? DecompilerReferenceFlags.Definition : DecompilerReferenceFlags.None;
			WriteText(text, reference, flags, ForcedColor ?? references.GetColor(reference, type));
		}

		public void WriteReference(IMember member, string text, bool isDefinition = false) {
			var reference = references.GetMemberReference(member);
			var flags = isDefinition ? DecompilerReferenceFlags.Definition : DecompilerReferenceFlags.None;
			WriteText(text, reference, flags, ForcedColor ?? references.GetColor(reference, member));
		}

		public void WriteLocalReference(string text, object reference, bool isDefinition = false, bool isHoverOnly = false) {
			var flags = DecompilerReferenceFlags.Local;
			if (isDefinition)
				flags |= DecompilerReferenceFlags.Definition;
			object? dnSpyReference;
			object color;
			switch (reference) {
			case ILVariable variable:
				dnSpyReference = variables.GetTextReference(variable);
				color = variable.Kind == VariableKind.Parameter ? BoxedTextColor.Parameter : BoxedTextColor.Local;
				break;

			case string label:
				// Labels: the method name and the label (unique within the decompiled text)
				dnSpyReference = label;
				color = BoxedTextColor.Label;
				break;

			case ICSharpCode.Decompiler.TypeSystem.IMethod localFunction when !isHoverOnly:
				dnSpyReference = references.Cache.ToDnlib(localFunction) ?? (object)variables.GetSyntheticVariable(localFunction, text);
				color = ReferenceFactory.GetColor(localFunction);
				break;

			case ISymbol symbol:
				// Hover only: members of 'dynamic' and the 'dynamic' type itself have no definition
				dnSpyReference = null;
				color = symbol is IType { Kind: TypeKind.Dynamic } ? BoxedTextColor.Keyword : ReferenceFactory.GetColor(symbol);
				break;

			default:
				// Range variables of query expressions ('let'): use an object dnSpy knows (tooltips)
				dnSpyReference = variables.GetSyntheticVariable(reference, text);
				color = BoxedTextColor.Local;
				break;
			}
			WriteText(text, dnSpyReference, flags, ForcedColor ?? color);
		}

		public void WriteXmlDocComment(string content) {
			FlushBuffer();
			WriteCore("///", null, DecompilerReferenceFlags.None, BoxedTextColor.XmlDocCommentDelimiter);
			int start = output.NextPosition;
			output.WriteXmlDoc(content);
			for (int i = content.IndexOf('\n'); i >= 0; i = content.IndexOf('\n', i + 1))
				lineStarts.Add(start + i + 1);
			WriteLine();
		}

		/// <summary>
		/// Writes text with a reference that isn't an upstream symbol (eg. comments with tokens)
		/// </summary>
		public void WriteRaw(string text, object? reference, DecompilerReferenceFlags flags, object color) {
			FlushBuffer();
			FlushPendingAt();
			WriteCore(text, reference, flags, color);
		}

		public void MarkFoldStart(string collapsedText = "...", bool defaultCollapsed = false, bool isDefinition = false) { }
		public void MarkDefinitionStart() { }
		public void MarkFoldEnd() { }

		public void AddBracePair(int leftStart, int leftEnd, int rightStart, int rightEnd, CodeBracesRangeFlags flags) {
			if (output.UsesCustomData)
				output.AddBracePair(TextSpan.FromBounds(leftStart, leftEnd), TextSpan.FromBounds(rightStart, rightEnd), flags);
		}

		public void AddLineSeparator(int position) {
			if (output.UsesCustomData)
				output.AddLineSeparator(position);
		}
	}
}
