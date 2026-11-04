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
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Output {
	/// <summary>
	/// Wraps upstream's <see cref="TextTokenWriter"/> (which decides what every identifier refers to) and adds what
	/// dnSpy needs: colors, brace pairs (brace matching, block structure and folding), line separators between
	/// members, colored XML documentation and comments with references.
	/// </summary>
	/// <remarks>
	/// It must be wrapped by <see cref="TokenWriter.WrapInWriterThatSetsLocationsInAST(TokenWriter)"/>, which
	/// needs the location of the next character (<see cref="ILocatable"/>).
	/// </remarks>
	sealed class DnSpyTokenWriter : DecoratingTokenWriter, ILocatable {
		readonly DnSpyTextOutput output;
		readonly Stack<AstNode> nodes = new Stack<AstNode>();
		readonly List<OpenBrace> openBraces = new List<OpenBrace>();
		// Position of the last '}' or ';' (where the line separator after a member is drawn)
		int lastMemberEnd = -1;
		int lastTokenStart;

		readonly struct OpenBrace {
			public readonly char Char;
			public readonly int Start;
			public readonly int End;
			public readonly AstNode? Node;
			public readonly CodeBracesRangeFlags Flags;
			public OpenBrace(char c, int start, int end, AstNode? node, CodeBracesRangeFlags flags) {
				Char = c;
				Start = start;
				End = end;
				Node = node;
				Flags = flags;
			}
		}

		readonly ICompilation? compilation;

		public DnSpyTokenWriter(DnSpyTextOutput output, DecompilerSettings settings, ICompilation? compilation)
			: base(new TextTokenWriter(output, settings)) {
			this.output = output;
			this.compilation = compilation;
		}

		public TextLocation Location => output.Location;
		public int Length => output.NextPosition;

		AstNode? CurrentNode => nodes.Count == 0 ? null : nodes.Peek();

		public override void StartNode(AstNode node) {
			if (IsMemberOrUsing(node))
				AddLineSeparatorBefore(node);
			nodes.Push(node);
			base.StartNode(node);
		}

		public override void EndNode(AstNode node) {
			base.EndNode(node);
			if (nodes.Count != 0)
				nodes.Pop();
			if (IsMemberOrUsing(node))
				lastMemberEnd = lastTokenStart;
		}

		static bool IsMemberOrUsing(AstNode node) =>
			(node is EntityDeclaration && !(node is Accessor) && !(node.Parent is LocalFunctionDeclarationStatement)) ||
			node is NamespaceDeclaration || IsUsing(node);

		static bool IsUsing(AstNode? node) => node is UsingDeclaration || node is UsingAliasDeclaration || node is ExternAliasDeclaration;

		/// <summary>
		/// Same as dnSpy's other decompilers: a separator after every type member (except between fields), and after
		/// the using declarations
		/// </summary>
		void AddLineSeparatorBefore(AstNode node) {
			if (lastMemberEnd < 0)
				return;
			if (!(node.Parent is TypeDeclaration || node.Parent is NamespaceDeclaration || node.Parent is SyntaxTree))
				return;
			var prev = node.PrevSibling;
			while (prev is not null && !IsMemberOrUsing(prev))
				prev = prev.PrevSibling;
			if (prev is null)
				return;
			if (prev is FieldDeclaration && node is FieldDeclaration)
				return;
			if (node is EnumMemberDeclaration)
				return;
			if (IsUsing(prev) && IsUsing(node))
				return;
			output.AddLineSeparator(lastMemberEnd);
		}

		public override void WriteIdentifier(Identifier identifier) {
			var node = CurrentNode;
			object color = BoxedTextColor.Text;
			object? reference = null;
			var referenceFlags = DecompilerReferenceFlags.None;
			object? forcedColor = null;
			if (node is not null && TryGetTypeParameter(node, identifier) is (ITypeParameter typeParameter, bool isTypeParameterDefinition)) {
				color = ReferenceFactory.GetColor(typeParameter);
				reference = output.References.GetTypeReference(typeParameter);
				if (isTypeParameterDefinition)
					referenceFlags = DecompilerReferenceFlags.Definition;
			}
			else if (node is not null && TryGetParameter(node) is object parameter) {
				// Parameters of methods without a body (abstract, interface, extern, delegates, indexers)
				color = BoxedTextColor.Parameter;
				reference = parameter;
				referenceFlags = DecompilerReferenceFlags.Local | DecompilerReferenceFlags.Definition;
			}
			else if (node?.GetSymbol() is INamespace ns) {
				color = BoxedTextColor.Namespace;
				reference = CreateNamespaceReference(ns, ns.FullName);
			}
			else if (node is not null && IsNamespaceName(node, out var nsName)) {
				color = BoxedTextColor.Namespace;
				reference = CreateNamespaceReference(FindNamespace(nsName), nsName);
			}
			else if (identifier.Parent is AstType && (identifier.Name == "var" || (identifier.Name == "global" && node?.GetSymbol() is null)))
				forcedColor = BoxedTextColor.Keyword;
			else if (node?.GetSymbol() is ISymbol symbol)
				color = ReferenceFactory.GetColor(symbol);
			Write(color, forcedColor, () => {
				output.IsIdentifier = true;
				output.IdentifierReference = reference;
				output.IdentifierReferenceFlags = referenceFlags;
				try {
					int start = output.NextPosition;
					base.WriteIdentifier(identifier);
					output.SetIdentifierEnd(identifier.EndLocation, start);
				}
				finally {
					output.IsIdentifier = false;
					output.IdentifierReference = null;
					output.IdentifierReferenceFlags = DecompilerReferenceFlags.None;
				}
			});
		}

		/// <summary>
		/// Upstream doesn't annotate type parameters (declarations and uses), find them by name in the enclosing
		/// declarations
		/// </summary>
		static (ITypeParameter? typeParameter, bool isDefinition) TryGetTypeParameter(AstNode node, Identifier identifier) {
			string name;
			bool isDefinition;
			if (node is TypeParameterDeclaration tpd) {
				name = tpd.Name;
				isDefinition = true;
			}
			else if (node is SimpleType st && st.TypeArguments.Count == 0) {
				if (st.GetSymbol() is ITypeParameter tp)
					return (tp, false);
				if (st.GetSymbol() is not null)
					return default;
				name = st.Identifier ?? string.Empty;
				isDefinition = false;
			}
			else
				return default;
			if (name != identifier.Name)
				return default;
			for (var ancestor = node.Parent; ancestor is not null; ancestor = ancestor.Parent) {
				var symbol = ancestor is LocalFunctionDeclarationStatement localFunction ? localFunction.GetSymbol() : ancestor is EntityDeclaration ? ancestor.GetSymbol() : null;
				var typeParameters = symbol switch {
					ICSharpCode.Decompiler.TypeSystem.IMethod method => method.TypeParameters,
					ITypeDefinition type => type.TypeParameters,
					_ => null,
				};
				if (typeParameters is null)
					continue;
				foreach (var tp in typeParameters) {
					if (tp.Name == name)
						return (tp, isDefinition);
				}
			}
			return default;
		}

		/// <summary>
		/// Gets the dnlib parameter of a parameter declaration that upstream didn't annotate with a variable
		/// </summary>
		object? TryGetParameter(AstNode node) {
			if (!(node is ParameterDeclaration pd) || !(pd.Parent is AstNode owner))
				return null;
			int index = 0;
			bool found = false;
			foreach (var child in owner.Children) {
				if (child == pd) {
					found = true;
					break;
				}
				if (child is ParameterDeclaration)
					index++;
			}
			if (!found)
				return null;
			var method = owner.GetSymbol() switch {
				ICSharpCode.Decompiler.TypeSystem.IMethod m => m,
				IProperty p => p.Getter ?? p.Setter,
				ITypeDefinition { Kind: TypeKind.Delegate } d => d.GetDelegateInvokeMethod(),
				_ => null,
			};
			if (method is null || !(output.References.Cache.ToDnlib(method) is dnlib.DotNet.MethodDef md))
				return null;
			int dnlibIndex = index + md.Parameters.MethodSigIndexBase;
			return (uint)dnlibIndex < (uint)md.Parameters.Count ? md.Parameters[dnlibIndex] : null;
		}

		/// <summary>
		/// The assembly is the one that defines the namespace's types (dnSpy shows the namespace in that assembly)
		/// </summary>
		NamespaceReference CreateNamespaceReference(INamespace? ns, string name) {
			dnlib.DotNet.IAssembly? assembly = null;
			if (ns is not null) {
				var mainModule = compilation?.MainModule;
				var type = ns.Types.FirstOrDefault(t => t.ParentModule == mainModule) ?? ns.Types.FirstOrDefault();
				var module = type?.ParentModule ?? ns.ContributingModules.FirstOrDefault();
				if (module?.MetadataFile is ICSharpCode.Decompiler.Metadata.MetadataFile file)
					assembly = output.References.Cache.Find(file)?.Module.Assembly;
			}
			return new NamespaceReference(assembly ?? output.References.Module.Assembly, name);
		}

		INamespace? FindNamespace(string name) {
			var ns = compilation?.RootNamespace;
			foreach (var part in name.Split('.')) {
				if (ns is null)
					break;
				ns = ns.GetChildNamespace(part);
			}
			return ns;
		}

		/// <summary>
		/// Names in using and namespace declarations aren't annotated with a namespace
		/// </summary>
		static bool IsNamespaceName(AstNode node, out string name) {
			name = string.Empty;
			if (!(node is AstType type))
				return false;
			AstNode? ancestor = node.Parent;
			while (ancestor is AstType)
				ancestor = ancestor.Parent;
			if (!(ancestor is UsingDeclaration || ancestor is NamespaceDeclaration || (ancestor is UsingAliasDeclaration && node.GetSymbol() is null)))
				return false;
			name = type.ToString();
			if (name.StartsWith("global::", StringComparison.Ordinal))
				name = name.Substring("global::".Length);
			return true;
		}

		public override void WriteKeyword(string keyword) {
			// Like dnSpy's previous C# decompiler, an accessor's keyword ('get', 'set'...) and an operator's 'operator'
			// keyword are their method's definition: the tree view and the edit dialog find the method there
			if (GetKeywordDefinition(keyword) is ICSharpCode.Decompiler.TypeSystem.IMethod method) {
				Write(BoxedTextColor.Keyword, BoxedTextColor.Keyword, () => output.WriteReference(method, keyword, isDefinition: true));
				return;
			}
			Write(BoxedTextColor.Keyword, BoxedTextColor.Keyword, () => base.WriteKeyword(keyword));
		}

		ICSharpCode.Decompiler.TypeSystem.IMethod? GetKeywordDefinition(string keyword) {
			switch (CurrentNode) {
			case Accessor accessor when keyword is "get" or "set" or "init" or "add" or "remove":
				return accessor.GetSymbol() as ICSharpCode.Decompiler.TypeSystem.IMethod;
			case OperatorDeclaration op when keyword == "operator":
				return op.GetSymbol() as ICSharpCode.Decompiler.TypeSystem.IMethod;
			case MethodDeclaration md when keyword == "operator" && md.Annotation<Transforms.CompoundAssignmentOperatorAnnotation>() is not null:
				return md.GetSymbol() as ICSharpCode.Decompiler.TypeSystem.IMethod;
			default:
				return null;
			}
		}

		public override void WritePrimitiveType(string type) {
			if (type == "new") {
				// 'new()' constraint
				output.WriteRaw("new", null, DecompilerReferenceFlags.None, BoxedTextColor.Keyword);
				int start = output.NextPosition;
				output.WriteRaw("(", null, DecompilerReferenceFlags.None, BoxedTextColor.Punctuation);
				output.WriteRaw(")", null, DecompilerReferenceFlags.None, BoxedTextColor.Punctuation);
				output.AddBracePair(start, start + 1, start + 1, start + 2, CodeBracesRangeFlags.Parentheses);
				return;
			}
			Write(BoxedTextColor.Keyword, BoxedTextColor.Keyword, () => base.WritePrimitiveType(type));
		}

		public override void WriteToken(string token) {
			var node = CurrentNode;
			int start = output.NextPosition;
			var color = GetTokenColor(token, node);
			// An expression-bodied property's getter has no 'get' keyword: its '=>' is the getter's definition
			if (token == "=>" && ((node as PropertyDeclaration)?.ExpressionBody is not null || (node as IndexerDeclaration)?.ExpressionBody is not null)) {
				var getter = (node!.GetSymbol() as IProperty)?.Getter;
				if (getter is not null) {
					Write(color, color, () => output.WriteReference(getter, token, isDefinition: true));
					lastTokenStart = start;
					return;
				}
			}
			Write(color, color, () => base.WriteToken(token));
			int end = output.NextPosition;
			lastTokenStart = start;
			if (token.Length == 1 && end == start + 1)
				AddBraces(token[0], start, end, node);
		}

		void AddBraces(char c, int start, int end, AstNode? node) {
			switch (c) {
			case '{':
				openBraces.Add(new OpenBrace(c, start, end, node, GetCurlyBraceFlags(node)));
				break;
			case '(':
				openBraces.Add(new OpenBrace(c, start, end, node, CodeBracesRangeFlags.Parentheses));
				break;
			case '[':
				openBraces.Add(new OpenBrace(c, start, end, node, CodeBracesRangeFlags.SquareBrackets));
				break;
			case '<':
				if (!IsOperatorNode(node))
					openBraces.Add(new OpenBrace(c, start, end, node, CodeBracesRangeFlags.AngleBrackets));
				break;
			case '}':
				CloseBrace('{', start, end, node);
				break;
			case ')':
				CloseBrace('(', start, end, node);
				break;
			case ']':
				CloseBrace('[', start, end, node);
				break;
			case '>':
				if (!IsOperatorNode(node))
					CloseBrace('<', start, end, node);
				break;
			}
		}

		void CloseBrace(char open, int start, int end, AstNode? node) {
			for (int i = openBraces.Count - 1; i >= 0; i--) {
				var brace = openBraces[i];
				if (brace.Char != open || brace.Node != node)
					continue;
				// Braces opened after it that weren't closed are dropped
				openBraces.RemoveRange(i, openBraces.Count - i);
				output.AddBracePair(brace.Start, brace.End, start, end, brace.Flags);
				if (open == '{')
					lastTokenStart = start;
				return;
			}
		}

		static bool IsOperatorNode(AstNode? node) =>
			node is BinaryOperatorExpression || node is UnaryOperatorExpression || node is AssignmentExpression ||
			node is OperatorDeclaration || (node is not null && node.GetType().Name.Contains("Pattern"));

		static object GetTokenColor(string token, AstNode? node) {
			if (node is InterpolatedStringExpression)
				return BoxedTextColor.String;
			switch (token) {
			case "(":
			case ")":
			case "[":
			case "]":
			case "{":
			case "}":
			case ",":
			case ";":
				return BoxedTextColor.Punctuation;
			case ":":
				return node is ConditionalExpression ? BoxedTextColor.Operator : BoxedTextColor.Punctuation;
			case "<":
			case ">":
				return IsOperatorNode(node) ? BoxedTextColor.Operator : BoxedTextColor.Punctuation;
			default:
				return BoxedTextColor.Operator;
			}
		}

		static CodeBracesRangeFlags GetCurlyBraceFlags(AstNode? node) {
			switch (node) {
			case BlockStatement block:
				return GetBlockFlags(block);
			case TypeDeclaration td:
				switch (td.ClassType) {
				case ClassType.Interface:
					return CodeBracesRangeFlags.InterfaceBraces;
				case ClassType.Struct:
				case ClassType.RecordStruct:
				case ClassType.Enum:
					return CodeBracesRangeFlags.ValueTypeBraces;
				default:
					return CodeBracesRangeFlags.TypeBraces;
				}
			case NamespaceDeclaration:
				return CodeBracesRangeFlags.NamespaceBraces;
			case PropertyDeclaration:
			case IndexerDeclaration:
				return CodeBracesRangeFlags.PropertyBraces;
			case CustomEventDeclaration:
				return CodeBracesRangeFlags.EventBraces;
			case SwitchStatement:
				return CodeBracesRangeFlags.SwitchBraces;
			case Interpolation:
				return CodeBracesRangeFlags.CurlyBraces;
			default:
				return CodeBracesRangeFlags.OtherBlockBraces;
			}
		}

		static CodeBracesRangeFlags GetBlockFlags(BlockStatement block) {
			switch (block.Parent) {
			case AnonymousMethodExpression:
			case LambdaExpression:
				return CodeBracesRangeFlags.AnonymousMethodBraces;
			case ConstructorDeclaration:
				return CodeBracesRangeFlags.ConstructorBraces;
			case DestructorDeclaration:
				return CodeBracesRangeFlags.DestructorBraces;
			case OperatorDeclaration:
				return CodeBracesRangeFlags.OperatorBraces;
			case MethodDeclaration md:
				return md.Parent is LocalFunctionDeclarationStatement ? CodeBracesRangeFlags.LocalFunctionBraces : CodeBracesRangeFlags.MethodBraces;
			case Accessor:
				return CodeBracesRangeFlags.AccessorBraces;
			case ForeachStatement:
			case ForStatement:
			case DoWhileStatement:
			case WhileStatement:
				return CodeBracesRangeFlags.LoopBraces;
			case IfElseStatement:
				return CodeBracesRangeFlags.ConditionalBraces;
			case TryCatchStatement tryCatch:
				if (tryCatch.TryBlock == block)
					return CodeBracesRangeFlags.TryBraces;
				if (tryCatch.FinallyBlock == block)
					return CodeBracesRangeFlags.FinallyBraces;
				return CodeBracesRangeFlags.OtherBlockBraces;
			case CatchClause:
				return CodeBracesRangeFlags.CatchBraces;
			case LockStatement:
				return CodeBracesRangeFlags.LockBraces;
			case UsingStatement:
				return CodeBracesRangeFlags.UsingBraces;
			case FixedStatement:
				return CodeBracesRangeFlags.FixedBraces;
			case SwitchSection:
				return CodeBracesRangeFlags.CaseBraces;
			default:
				return CodeBracesRangeFlags.OtherBlockBraces;
			}
		}

		public override void WritePrimitiveValue(object? value, LiteralFormat format = LiteralFormat.None) {
			object color = value switch {
				null or bool => BoxedTextColor.Keyword,
				string => format == LiteralFormat.VerbatimStringLiteral ? BoxedTextColor.VerbatimString : BoxedTextColor.String,
				char => BoxedTextColor.Char,
				_ => BoxedTextColor.Number,
			};
			output.BeginBuffer(color);
			string text;
			int start;
			try {
				base.WritePrimitiveValue(value, format);
			}
			finally {
				(text, start) = output.EndBuffer();
			}
			lastTokenStart = start;
			char quote = value is string ? '"' : value is char ? '\'' : '\0';
			if (quote != '\0') {
				int first = text.IndexOf(quote);
				int last = text.LastIndexOf(quote);
				if (first >= 0 && last > first)
					output.AddBracePair(start + first, start + first + 1, start + last, start + last + 1, quote == '"' ? CodeBracesRangeFlags.DoubleQuotes : CodeBracesRangeFlags.SingleQuotes);
			}
		}

		public override void WriteInterpolatedText(string text) =>
			Write(BoxedTextColor.String, BoxedTextColor.String, () => base.WriteInterpolatedText(text));

		public override void Space() => Write(BoxedTextColor.Text, null, () => base.Space());

		public override void NewLine() => Write(BoxedTextColor.Text, null, () => base.NewLine());

		public override void WriteComment(CommentType commentType, string content) {
			lastTokenStart = output.NextPosition;
			switch (commentType) {
			case CommentType.Documentation when content.IndexOf('\n') < 0:
				output.WriteXmlDocComment(content);
				break;
			case CommentType.SingleLine:
				if (CurrentNode?.Annotation<TokenCommentAnnotation>() is TokenCommentAnnotation tokenComment)
					tokenComment.Write(output);
				else {
					output.WriteRaw("//", null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
					output.WriteRaw(content, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				}
				output.WriteLine();
				break;
			case CommentType.MultiLine:
				output.WriteRaw("/*" + content + "*/", null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				break;
			case CommentType.MultiLineDocumentation:
				output.WriteRaw("/**" + content + "*/", null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				break;
			case CommentType.InactiveCode:
				output.WriteRaw(content, null, DecompilerReferenceFlags.None, BoxedTextColor.ExcludedCode);
				break;
			default:
				output.WriteRaw(content, null, DecompilerReferenceFlags.None, BoxedTextColor.Comment);
				break;
			}
		}

		public override void WritePreProcessorDirective(PreProcessorDirectiveType type, string? argument) {
			output.WriteRaw("#" + type.ToString().ToLowerInvariant(), null, DecompilerReferenceFlags.None, BoxedTextColor.PreprocessorKeyword);
			if (!string.IsNullOrEmpty(argument)) {
				output.WriteRaw(" ", null, DecompilerReferenceFlags.None, BoxedTextColor.Text);
				output.WriteRaw(argument!, null, DecompilerReferenceFlags.None, BoxedTextColor.PreprocessorText);
			}
			output.WriteLine();
		}

		void Write(object textColor, object? forcedColor, Action write) {
			var oldTextColor = output.TextColor;
			var oldForcedColor = output.ForcedColor;
			output.TextColor = textColor;
			output.ForcedColor = forcedColor;
			try {
				write();
			}
			finally {
				output.TextColor = oldTextColor;
				output.ForcedColor = oldForcedColor;
			}
		}
	}
}
