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
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Syntax.PatternMatching;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core.Transforms {
	/// <summary>
	/// An <see cref="ArrayInitializerExpression"/> that is a C# 12 collection expression ('[a, b, .. c]'), written by
	/// <see cref="Output.DnSpyOutputVisitor"/>
	/// </summary>
	sealed class CollectionExpressionAnnotation {
		public IType ElementType { get; }
		public CollectionExpressionAnnotation(IType elementType) => ElementType = elementType;
	}

	/// <summary>
	/// A spread element ('.. c') of a collection expression: a range without a start
	/// </summary>
	sealed class SpreadElementAnnotation {
		public static readonly SpreadElementAnnotation Instance = new SpreadElementAnnotation();
		SpreadElementAnnotation() { }
	}

	/// <summary>
	/// C# 12 collection expressions. Upstream 11.2 doesn't decompile them, it shows the code the compiler generates:
	/// <list type="bullet">
	/// <item>The compiler's own types for interface targets ('IReadOnlyList&lt;int&gt; x = [a, b]'), which don't compile:
	/// 'new &lt;&gt;z__ReadOnlyArray&lt;int&gt;(new int[2] { a, b })', '&lt;&gt;z__ReadOnlySingleElementList', '&lt;&gt;z__ReadOnlyList'</item>
	/// <item>Code that fills a list, an array or an inline array (spans, params spans): 'List&lt;int&gt; list = new List&lt;int&gt;(3);
	/// CollectionsMarshal.SetCount(list, 3); Span&lt;int&gt; span = CollectionsMarshal.AsSpan(list); span[0] = a; ...',
	/// 'int[] array = new int[1 + xs.Length]; ... CopyTo(...)', '&lt;&gt;y__InlineArray3&lt;int&gt; buffer = default; buffer[0] = a; ...'</item>
	/// </list>
	/// This turns them into collection expressions. Each collection is the same as the compiler's (same type, same
	/// elements in the same order): where the context doesn't make the collection expression create that type, it's cast
	/// ('(IReadOnlyList&lt;int&gt;)[a, b]'). Code that doesn't match exactly is left as it is. A no-op on upstream output
	/// that already has collection expressions (it has no such code).
	/// </summary>
	sealed class CollectionExpressionTransform : IAstTransform {
		const string CompilerTypePrefix = "<>z__ReadOnly";

		TransformContext context = null!;
		AstNode root = null!;
		Dictionary<ILVariable, List<IdentifierExpression>> references = null!;
		readonly List<(ILVariable variable, Statement statement)> keptDefinitions = new List<(ILVariable, Statement)>();
		readonly List<Statement> statementsWithCollections = new List<Statement>();

		public void Run(AstNode rootNode, TransformContext context) {
			this.context = context;
			root = rootNode;
			references = new Dictionary<ILVariable, List<IdentifierExpression>>();
			foreach (var identifier in rootNode.Descendants.OfType<IdentifierExpression>()) {
				if (identifier.GetILVariable() is ILVariable variable) {
					if (!references.TryGetValue(variable, out var list))
						references.Add(variable, list = new List<IdentifierExpression>());
					list.Add(identifier);
				}
			}

			foreach (var block in rootNode.Descendants.OfType<BlockStatement>().ToArray()) {
				if (!IsAttached(block))
					continue;
				for (int i = 0; i < block.Statements.Count; i++) {
					var statement = block.Statements.ElementAt(i);
					if (TryCollapseBuilder(block, statement))
						i = -1; // the statements changed; start over (builders are short)
				}
			}

			RemoveUnusedDefinitions();
			InlineTemporaries();

			// Inner ones first
			foreach (var creation in rootNode.Descendants.OfType<ObjectCreateExpression>().Reverse().ToArray()) {
				if (creation.Parent is not null)
					TryReplaceCompilerType(creation);
			}
			foreach (var invocation in rootNode.Descendants.OfType<InvocationExpression>().Reverse().ToArray()) {
				if (invocation.Parent is not null)
					TryReplaceImmutableArray(invocation);
			}
			keptDefinitions.Clear();
			statementsWithCollections.Clear();
		}

		#region Compiler types for interfaces
		enum CompilerTypeKind { Array, List, SingleElement }

		static bool IsCompilerType(IType type, out CompilerTypeKind kind, out IType elementType) {
			kind = default;
			elementType = null!;
			if (type.GetDefinition() is not ITypeDefinition td || td.DeclaringTypeDefinition is not null || td.Namespace.Length != 0 || td.TypeParameterCount != 1)
				return false;
			if (GetCompilerTypeKind(td.Name) is not CompilerTypeKind k)
				return false;
			kind = k;
			if (!td.GetAttributes().Any(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.CompilerGeneratedAttribute"))
				return false;
			if (type.TypeArguments.Count != 1)
				return false;
			elementType = type.TypeArguments[0];
			return true;
		}

		/// <summary>
		/// The compiler's type that implements a collection expression for an interface (the tree view hides it)
		/// </summary>
		internal static bool IsCompilerType(dnlib.DotNet.TypeDef type) =>
			type.DeclaringType is null && UTF8StringIsEmpty(type.Namespace) && type.GenericParameters.Count == 1 &&
			type.Name.String.EndsWith("`1", StringComparison.Ordinal) && GetCompilerTypeKind(type.Name.String.Substring(0, type.Name.String.Length - 2)) is not null &&
			type.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute");

		/// <summary>
		/// The kind of compiler type named <paramref name="name"/> (without the generic arity). When an edit compiles code
		/// that uses one into a module that already has it, dnSpy's importer adds the compiled copy with "__N" after the
		/// name ('&lt;&gt;z__ReadOnlyArray__0'), so that name is one too.
		/// </summary>
		static CompilerTypeKind? GetCompilerTypeKind(string name) {
			if (!name.StartsWith(CompilerTypePrefix, StringComparison.Ordinal))
				return null;
			int end = name.Length;
			int index = name.LastIndexOf("__", StringComparison.Ordinal);
			if (index > CompilerTypePrefix.Length && index + 2 < name.Length && name.Skip(index + 2).All(c => c >= '0' && c <= '9'))
				end = index;
			switch (name.Substring(CompilerTypePrefix.Length, end - CompilerTypePrefix.Length)) {
			case "Array": return CompilerTypeKind.Array;
			case "List": return CompilerTypeKind.List;
			case "SingleElementList": return CompilerTypeKind.SingleElement;
			default: return null;
			}
		}

		static bool UTF8StringIsEmpty(dnlib.DotNet.UTF8String? s) => dnlib.DotNet.UTF8String.IsNullOrEmpty(s);

		void TryReplaceCompilerType(ObjectCreateExpression creation) {
			if (creation.GetSymbol() is not IMethod { IsConstructor: true } ctor || !IsCompilerType(ctor.DeclaringType, out var kind, out var elementType))
				return;
			if (creation.Arguments.Count != 1 || creation.Initializer is { Elements.Count: > 0 })
				return;
			var argument = creation.Arguments.Single();
			List<Expression> elements;
			switch (kind) {
			case CompilerTypeKind.SingleElement:
				elements = new List<Expression> { argument.Detach() };
				break;

			case CompilerTypeKind.Array:
				if (argument is ArrayCreateExpression { Initializer: ArrayInitializerExpression initializer } arrayCreation && arrayCreation.AdditionalArraySpecifiers.Count == 0 &&
					arrayCreation.Arguments.Count <= 1 && (arrayCreation.Arguments.Count == 0 || IsConstant(arrayCreation.Arguments.Single(), initializer.Elements.Count))) {
					elements = initializer.Elements.Select(a => a.Detach()).ToList();
					CopyILAnnotations(arrayCreation, creation);
					CopyILAnnotations(initializer, creation);
				}
				else if (argument.Annotation<CollectionExpressionAnnotation>() is not null)
					elements = ((ArrayInitializerExpression)argument).Elements.Select(a => a.Detach()).ToList();
				else if (argument is InvocationExpression { Target: MemberReferenceExpression { MemberName: "ToArray" } toArray, Arguments.Count: 0 } &&
					argument.GetSymbol() is IMethod { Parameters.Count: 0, IsStatic: false } listToArray && IsType(listToArray.DeclaringType, "System.Collections.Generic.List", 1)) {
					// [.. list] (List<T>.ToArray())
					elements = new List<Expression> { CreateSpread(toArray.Target.Detach()) };
					CopyILAnnotations(argument, creation);
				}
				else if (argument is InvocationExpression { Target: MemberReferenceExpression { MemberName: "ToArray" } } && argument.GetSymbol() is IMethod { IsExtensionMethod: true, Parameters.Count: 1 } enumerableToArray &&
					enumerableToArray.DeclaringType.FullName == "System.Linq.Enumerable" &&
					argument.GetResolveResult() is CSharpInvocationResolveResult { IsExtensionMethodInvocation: true }) {
					// [.. e] (Enumerable.ToArray(e))
					var source = ((MemberReferenceExpression)((InvocationExpression)argument).Target).Target;
					elements = new List<Expression> { CreateSpread(source.Detach()) };
					CopyILAnnotations(argument, creation);
				}
				else
					elements = new List<Expression> { CreateSpread(argument.Detach()) };
				break;

			case CompilerTypeKind.List:
				if (argument.Annotation<CollectionExpressionAnnotation>() is not null)
					elements = ((ArrayInitializerExpression)argument).Elements.Select(a => a.Detach()).ToList();
				else
					elements = new List<Expression> { CreateSpread(argument.Detach()) };
				break;

			default:
				return;
			}
			var collection = CreateCollection(elements, elementType, ctor.DeclaringType);
			collection.CopyAnnotationsFrom(creation);
			collection.RemoveAnnotations<ResolveResult>();
			collection.AddAnnotation(new ResolveResult(ctor.DeclaringType));
			Replace(creation, collection, ctor.DeclaringType, type => IsReadOnlyInterface(type, elementType), CreateReadOnlyListType(elementType));
		}

		/// <summary>
		/// Same type, ignoring nullable annotations
		/// </summary>
		static bool SameType(IType a, IType b) {
			a = a.ChangeNullability(Nullability.Oblivious);
			b = b.ChangeNullability(Nullability.Oblivious);
			if (a.Kind != b.Kind)
				return false;
			if (a is ArrayType arrayA)
				return b is ArrayType arrayB && arrayA.Dimensions == arrayB.Dimensions && SameType(arrayA.ElementType, arrayB.ElementType);
			var definitionA = a.GetDefinition();
			var definitionB = b.GetDefinition();
			if (definitionA is null || definitionB is null)
				return a.Equals(b);
			if (!definitionA.Equals(definitionB) || a.TypeArguments.Count != b.TypeArguments.Count)
				return false;
			for (int i = 0; i < a.TypeArguments.Count; i++) {
				if (!SameType(a.TypeArguments[i], b.TypeArguments[i]))
					return false;
			}
			return true;
		}

		// The compiler uses its types for these targets
		static bool IsReadOnlyInterface(IType type, IType elementType) =>
			type.TypeArguments.Count == 1 && SameType(type.TypeArguments[0], elementType) && type.Kind == TypeKind.Interface &&
			type.FullName is "System.Collections.Generic.IEnumerable" or "System.Collections.Generic.IReadOnlyCollection" or "System.Collections.Generic.IReadOnlyList";

		IType CreateReadOnlyListType(IType elementType) {
			var definition = context.TypeSystem.FindType(new FullTypeName("System.Collections.Generic.IReadOnlyList`1")).GetDefinition();
			if (definition is null)
				return elementType;
			return new ParameterizedType(definition, new[] { elementType });
		}
		#endregion

		#region Collection expressions and their target type
		ArrayInitializerExpression CreateCollection(IEnumerable<Expression> elements, IType elementType, IType type) {
			var collection = new ArrayInitializerExpression();
			collection.AddAnnotation(new CollectionExpressionAnnotation(elementType));
			foreach (var element in elements) {
				// A range value (not a spread) must be in parentheses: '..x' is a spread
				if (element is BinaryOperatorExpression { Operator: BinaryOperatorType.Range } && element.Annotation<SpreadElementAnnotation>() is null)
					collection.Elements.Add(new ParenthesizedExpression(element));
				else
					collection.Elements.Add(element);
			}
			collection.AddAnnotation(new ResolveResult(type));
			return collection;
		}

		static Expression CreateSpread(Expression source) {
			var spread = new BinaryOperatorExpression { Operator = BinaryOperatorType.Range, Right = source };
			spread.AddAnnotation(SpreadElementAnnotation.Instance);
			spread.AddAnnotation(new ResolveResult(source.GetResolveResult()?.Type ?? SpecialType.UnknownType));
			return spread;
		}

		/// <summary>
		/// Replaces an expression of type <paramref name="type"/> with a collection expression that creates the same
		/// collection: where the target type of its position is <paramref name="isSameCollection"/>, as it is, else cast
		/// to the target type (if it gives the same collection) or to <paramref name="castType"/>
		/// </summary>
		void Replace(Expression expression, ArrayInitializerExpression collection, IType type, Func<IType, bool> isSameCollection, IType castType) {
			var (targetType, targetTyped) = GetTargetType(expression);
			Expression replacement = collection;
			if (targetType is null || !isSameCollection(targetType) || !targetTyped) {
				var cast = targetType is not null && isSameCollection(targetType) ? targetType : castType;
				replacement = new CastExpression(context.TypeSystemAstBuilder.ConvertType(cast), collection);
				replacement.AddAnnotation(new ConversionResolveResult(cast, new ResolveResult(type), Conversion.IdentityConversion));
			}
			expression.ReplaceWith(replacement);
		}

		/// <summary>
		/// The type that the position of an expression converts it to, and whether a collection expression there gets it
		/// as its target type
		/// </summary>
		(IType? type, bool targetTyped) GetTargetType(Expression expression) {
			var parent = expression.Parent;
			switch (parent) {
			case ParenthesizedExpression parenthesized:
				return GetTargetType(parenthesized);

			case CastExpression cast:
				return (cast.Type.GetResolveResult()?.Type ?? cast.GetResolveResult()?.Type, true);

			case VariableInitializer variable when variable.Initializer == expression:
				if (variable.Parent is VariableDeclarationStatement declaration) {
					if (declaration.Type is SimpleType { Identifier: "var" } || declaration.Variables.Count != 1)
						return (null, false);
					return (variable.GetILVariable()?.Type ?? declaration.Type.GetResolveResult()?.Type, true);
				}
				if (variable.Parent is FieldDeclaration fieldDeclaration)
					return ((variable.GetSymbol() ?? fieldDeclaration.GetSymbol()) is IField field ? field.ReturnType : null, true);
				return (null, false);

			case PropertyDeclaration property when property.Initializer == expression:
				return (property.GetSymbol() is IProperty p ? p.ReturnType : null, true);

			case AssignmentExpression assignment when assignment.Right == expression && assignment.Operator == AssignmentOperatorType.Assign:
				return (assignment.Left.GetResolveResult()?.Type, true);

			case ReturnStatement:
				return GetReturnType(parent);

			case LambdaExpression lambda when lambda.Body == expression:
				return GetReturnType(expression);

			case ArrayInitializerExpression elements:
				if (elements.Annotation<CollectionExpressionAnnotation>() is CollectionExpressionAnnotation outer)
					return (outer.ElementType, true);
				if (elements.Parent is ArrayCreateExpression arrayCreation && arrayCreation.GetResolveResult()?.Type is ArrayType { Dimensions: 1 } arrayType)
					return (arrayType.ElementType, true);
				return (null, false);

			case InvocationExpression:
			case ObjectCreateExpression:
				return GetParameterType(expression, (Expression)parent);

			default:
				return (null, false);
			}
		}

		// The return type of the method, accessor, local function or lambda that returns the value
		(IType? type, bool targetTyped) GetReturnType(AstNode node) {
			foreach (var ancestor in node.Ancestors) {
				switch (ancestor) {
				case LambdaExpression:
				case AnonymousMethodExpression:
					// The lambda's return type is only known if the lambda has a target type
					var function = ancestor.Annotation<ILFunction>();
					bool explicitDelegateType = ancestor.Parent is VariableInitializer { Parent: VariableDeclarationStatement { Type: not SimpleType { Identifier: "var" } } } ||
						ancestor.Parent is AssignmentExpression || ancestor.Parent is CastExpression;
					return (function is null ? null : function.AsyncReturnType ?? function.ReturnType, explicitDelegateType);

				case LocalFunctionDeclarationStatement:
				case EntityDeclaration:
					var ilFunction = ancestor.Annotation<ILFunction>();
					if (ilFunction is not null)
						return (ilFunction.AsyncReturnType ?? ilFunction.ReturnType, !ilFunction.IsIterator);
					if (ancestor.GetSymbol() is IMethod method && !(ancestor is EntityDeclaration entity && entity.HasModifier(Modifiers.Async)))
						return (method.ReturnType, true);
					if (ancestor.GetSymbol() is IProperty property)
						return (property.ReturnType, true);
					return (null, false);
				}
			}
			return (null, false);
		}

		// The parameter type of an argument. A collection expression gets it as target type if the method has no
		// overloads and type arguments to infer (they could resolve differently).
		static (IType? type, bool targetTyped) GetParameterType(Expression argument, Expression call) {
			var arguments = call is InvocationExpression invocation ? invocation.Arguments : ((ObjectCreateExpression)call).Arguments;
			if (call.GetSymbol() is not IMethod method || arguments.Any(a => a is NamedArgumentExpression))
				return (null, false);
			int index = arguments.TakeWhile(a => a != argument).Count();
			if (call.GetResolveResult() is CSharpInvocationResolveResult { IsExtensionMethodInvocation: true } && call is InvocationExpression { Target: MemberReferenceExpression { Target: not TypeReferenceExpression } })
				index++;
			int argumentCount = arguments.Count + (index > arguments.TakeWhile(a => a != argument).Count() ? 1 : 0);
			// (params: only the normal form, the argument is the collection)
			if (index >= method.Parameters.Count || (method.Parameters[index].IsParams && argumentCount != method.Parameters.Count))
				return (null, false);
			var type = method.Parameters[index].Type;
			bool hasTypeArguments = method.TypeParameters.Count != 0 && !(call is InvocationExpression { Target: MemberReferenceExpression { TypeArguments.Count: > 0 } or IdentifierExpression { TypeArguments.Count: > 0 } });
			bool overloaded = method.IsExtensionMethod || method.DeclaringTypeDefinition is not ITypeDefinition declaringType ||
				(method.IsConstructor ? declaringType.GetConstructors().Count() : declaringType.GetMethods(m => m.Name == method.Name).Count()) > 1;
			return (type, !hasTypeArguments && !overloaded);
		}
		#endregion

		#region Builders
		enum BuilderKind { List, Array, InlineArray }

		sealed class Builder {
			public ILVariable Variable = null!;
			public BuilderKind Kind;
			public IType ElementType = null!;
			public Statement Anchor = null!;
			public Expression AnchorValue = null!;
			public readonly List<Statement> Statements = new List<Statement>();
			public readonly List<(Expression expression, bool spread)> Elements = new List<(Expression, bool)>();
			// Variables (other than the builder) defined in the statements, in order. Internal: the span of a list
			// (only the builder uses it).
			public readonly List<(ILVariable variable, Statement statement, Expression value, bool isInternal)> Temporaries = new List<(ILVariable, Statement, Expression, bool)>();
			public ILVariable? IndexVariable;
			// Where the elements are stored: the array or inline array, the span of a list, or the list's span itself
			// ('CollectionsMarshal.AsSpan(list)[0] = a;')
			public ILVariable? StoreTarget;
			public bool DirectSpan;
			public bool HasStores => StoreTarget is not null || DirectSpan;
			// List<T>: the capacity / count, array: the length, inline array: its length
			public Expression? Length;
			public int InlineArrayLength;
		}

		/// <summary>
		/// A statement 'T v = value;' or 'v = value;'
		/// </summary>
		static bool MatchDefinition(Statement statement, out ILVariable variable, out Expression value) {
			variable = null!;
			value = null!;
			switch (statement) {
			case VariableDeclarationStatement { Variables.Count: 1 } declaration:
				var initializer = declaration.Variables.Single();
				if (initializer.GetILVariable() is not ILVariable v || initializer.Initializer is not Expression init)
					return false;
				variable = v;
				value = init;
				return true;
			case ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Left: IdentifierExpression left } assignment }:
				if (left.GetILVariable() is not ILVariable assigned)
					return false;
				variable = assigned;
				value = assignment.Right;
				return true;
			default:
				return false;
			}
		}

		static bool IsVariable(AstNode? node, ILVariable? variable) => variable is not null && node is IdentifierExpression identifier && identifier.GetILVariable() == variable;

		static bool IsConstant(AstNode node, int value) => node is PrimitiveExpression { Value: int v } && v == value;

		static bool IsType(IType? type, string fullName, int typeArguments) =>
			type is not null && type.FullName == fullName && type.TypeParameterCount == typeArguments;

		static bool IsMethod(AstNode node, string declaringType, string name, out IMethod method) {
			method = null!;
			if (node.GetSymbol() is not IMethod m || m.Name != name || m.DeclaringType.FullName != declaringType)
				return false;
			method = m;
			return true;
		}

		// Arguments of a call, with the instance ('list.Add(x)': list, x) or the 'this' argument of an extension method
		static IReadOnlyList<Expression> GetArguments(InvocationExpression invocation) {
			if (invocation.Target is MemberReferenceExpression { Target: not TypeReferenceExpression } member &&
				(invocation.GetSymbol() is IMethod { IsStatic: false } || invocation.GetResolveResult() is CSharpInvocationResolveResult { IsExtensionMethodInvocation: true }))
				return new[] { member.Target }.Concat(invocation.Arguments).ToArray();
			return invocation.Arguments.ToArray();
		}

		static Expression? GetReceiver(InvocationExpression invocation) =>
			invocation.Target is MemberReferenceExpression member && invocation.GetSymbol() is IMethod { IsStatic: false } ? member.Target : null;

		bool TryCollapseBuilder(BlockStatement block, Statement anchor) {
			if (!MatchDefinition(anchor, out var variable, out var value))
				return false;
			var builder = new Builder { Variable = variable, Anchor = anchor, AnchorValue = value };
			var type = variable.Type;
			if (value is ObjectCreateExpression { Initializer: null or { Elements.Count: 0 } } listCreation && IsType(type, "System.Collections.Generic.List", 1) &&
				listCreation.GetResolveResult()?.Type is IType createdType && SameType(type, createdType)) {
				if (listCreation.Arguments.Count > 1)
					return false;
				builder.Kind = BuilderKind.List;
				builder.ElementType = type.TypeArguments[0];
				builder.Length = listCreation.Arguments.SingleOrDefault();
			}
			else if (value is ArrayCreateExpression { Initializer: null or { Elements.Count: 0 }, Arguments.Count: 1, AdditionalArraySpecifiers.Count: 0 } arrayCreation && type is ArrayType { Dimensions: 1 } arrayType &&
				arrayCreation.GetResolveResult()?.Type is IType createdArrayType && SameType(type, createdArrayType)) {
				builder.Kind = BuilderKind.Array;
				builder.ElementType = arrayType.ElementType;
				builder.Length = arrayCreation.Arguments.Single();
			}
			else if (value is DefaultValueExpression && GetInlineArrayLength(type) is int length && type.TypeArguments.Count == 1) {
				builder.Kind = BuilderKind.InlineArray;
				builder.ElementType = type.TypeArguments[0];
				builder.InlineArrayLength = length;
			}
			else
				return false;

			var statements = block.Statements.ToList();
			int anchorIndex = statements.IndexOf(anchor);
			if (!MatchOperations(builder, statements, anchorIndex + 1, out int end) || builder.Elements.Count == 0)
				return false;

			// Temporaries before the builder that its operations use (the compiler evaluates elements before the
			// collection is created)
			var used = new HashSet<ILVariable>(statements.Skip(anchorIndex).Take(end - anchorIndex).SelectMany(s => s.DescendantsAndSelf.OfType<IdentifierExpression>())
				.Select(a => a.GetILVariable()).OfType<ILVariable>());
			int start = anchorIndex;
			while (start > 0 && MatchDefinition(statements[start - 1], out var temporary, out var temporaryValue) && used.Contains(temporary) && temporary != variable &&
				!builder.Temporaries.Any(a => a.variable == temporary)) {
				start--;
				builder.Temporaries.Insert(0, (temporary, statements[start], temporaryValue, false));
				foreach (var v in temporaryValue.DescendantsAndSelf.OfType<IdentifierExpression>().Select(a => a.GetILVariable()).OfType<ILVariable>())
					used.Add(v);
			}
			for (int i = start; i < end; i++)
				builder.Statements.Add(statements[i]);
			if (!ValidateLength(builder))
				return false;
			return Rewrite(block, builder, end < statements.Count ? statements[end] : null);
		}

		static int? GetInlineArrayLength(IType type) {
			if (type.Kind != TypeKind.Struct || type.GetDefinition() is not ITypeDefinition td)
				return null;
			var attribute = td.GetAttributes().FirstOrDefault(a => a.AttributeType.FullName == "System.Runtime.CompilerServices.InlineArrayAttribute");
			if (attribute is null || attribute.FixedArguments.Length != 1 || attribute.FixedArguments[0].Value is not int length || length <= 0)
				return null;
			return length;
		}

		/// <summary>
		/// The statements that fill the builder: adds, stores, copies of spreads. Ends at the first other statement.
		/// Returns false if a statement looks like part of it but doesn't match.
		/// </summary>
		bool MatchOperations(Builder builder, List<Statement> statements, int index, out int end) {
			var builderVariable = builder.Variable;
			builder.StoreTarget = builder.Kind == BuilderKind.List ? null : builderVariable;
			int constantIndex = 0;
			bool usesIndexVariable = false;
			end = index;
			for (; index < statements.Count; index++) {
				var statement = statements[index];
				if (builder.Kind == BuilderKind.List && !builder.HasStores && builder.Elements.Count == 0 &&
					statement is ExpressionStatement { Expression: InvocationExpression setCount } && IsMethod(setCount, "System.Runtime.InteropServices.CollectionsMarshal", "SetCount", out _)) {
					// List<T> with a known count: SetCount(list, n); Span<T> span = AsSpan(list); stores
					if (setCount.Arguments.Count != 2 || !IsVariable(setCount.Arguments.First(), builderVariable))
						return false;
					builder.Length ??= setCount.Arguments.Last();
					if (index + 1 < statements.Count && MatchDefinition(statements[index + 1], out var span, out var spanValue) &&
						spanValue is InvocationExpression asSpan && IsListSpan(builder, asSpan)) {
						builder.Temporaries.Add((span, statements[index + 1], spanValue, true));
						builder.StoreTarget = span;
						index++;
					}
					else
						builder.DirectSpan = true;
					end = index + 1;
					continue;
				}

				// ref T element = ref target[i]; (temporaries;) element = value;   (the elements of a nested collection)
				if (builder.HasStores && statement is VariableDeclarationStatement && MatchDefinition(statement, out var element, out var elementReference) &&
					elementReference is DirectionExpression { FieldDirection: FieldDirection.Ref, Expression: IndexerExpression { Arguments.Count: 1 } elementStore } &&
					IsStoreTarget(builder, elementStore.Target)) {
					int valueIndex = index + 1;
					var temporaries = new List<(ILVariable, Statement, Expression, bool)>();
					while (valueIndex < statements.Count && MatchDefinition(statements[valueIndex], out var temporary, out var temporaryValue) && temporary != element &&
						temporary != builderVariable && IsSideEffectFreeOrSpan(temporaryValue)) {
						temporaries.Add((temporary, statements[valueIndex], temporaryValue, false));
						valueIndex++;
					}
					if (valueIndex >= statements.Count || !MatchDefinition(statements[valueIndex], out var assigned, out var elementValue) || assigned != element ||
						statements[valueIndex] is not ExpressionStatement)
						return false;
					if (!MatchStoreIndex(builder, elementStore.Arguments.Single(), ref constantIndex, ref usesIndexVariable))
						return false;
					builder.Temporaries.Add((element, statement, elementReference, true));
					builder.Temporaries.AddRange(temporaries);
					builder.Elements.Add((elementValue, false));
					index = valueIndex;
					end = index + 1;
					continue;
				}

				var result = MatchOperation(builder, statement, ref constantIndex, ref usesIndexVariable);
				if (result is null)
					break;
				if (result == false)
					return false;
				end = index + 1;
				if (builder.Kind == BuilderKind.InlineArray && builder.Elements.Count == builder.InlineArrayLength)
					break;
			}
			// Temporaries at the end that no operation uses aren't part of it
			while (builder.Temporaries.Count != 0 && end > 0 && builder.Temporaries[builder.Temporaries.Count - 1].statement == statements[end - 1] &&
				!IsUsedIn(builder.Temporaries[builder.Temporaries.Count - 1].variable, builder.Elements.Select(a => a.expression))) {
				builder.Temporaries.RemoveAt(builder.Temporaries.Count - 1);
				end--;
			}
			if (builder.Kind == BuilderKind.InlineArray && builder.Elements.Count != builder.InlineArrayLength)
				return false;
			return true;
		}

		// true: an operation, false: it looks like one but doesn't match, null: not an operation
		bool? MatchOperation(Builder builder, Statement statement, ref int constantIndex, ref bool usesIndexVariable) {
			var builderVariable = builder.Variable;
			switch (statement) {
			// target[i] = value; target[index] = value;
			case ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Left: IndexerExpression { Arguments.Count: 1 } store } assignment }
				when IsStoreTarget(builder, store.Target): {
				if (!MatchStoreIndex(builder, store.Arguments.Single(), ref constantIndex, ref usesIndexVariable))
					return false;
				builder.Elements.Add((assignment.Right, false));
				return true;
			}

			// index++; index += span.Length; index += collection.Count;
			case ExpressionStatement when builder.IndexVariable is not null && IsIncrement(statement, builder.IndexVariable):
				return true;
			case ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Add } addLength } when builder.IndexVariable is not null && IsVariable(addLength.Left, builder.IndexVariable):
				return true;
			case ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Right: BinaryOperatorExpression { Operator: BinaryOperatorType.Add } sum } addLength }
				when builder.IndexVariable is not null && IsVariable(addLength.Left, builder.IndexVariable) && IsVariable(sum.Left, builder.IndexVariable):
				return true;

			// span.CopyTo(new Span<T>(array).Slice(index, span.Length)); span.CopyTo(listSpan.Slice(index, span.Length));
			case ExpressionStatement { Expression: InvocationExpression { Arguments.Count: 1 } copyTo } when builder.HasStores && copyTo.GetSymbol() is IMethod { Name: "CopyTo" } copyMethod &&
				(IsType(copyMethod.DeclaringType, "System.ReadOnlySpan", 1) || IsType(copyMethod.DeclaringType, "System.Span", 1)): {
				if (GetReceiver(copyTo) is not Expression spanSource)
					return false;
				if (copyTo.Arguments.Single() is not InvocationExpression { Target: MemberReferenceExpression { MemberName: "Slice", Target: var sliced }, Arguments.Count: 2 } slice)
					return false;
				if (!(IsStoreTarget(builder, sliced) || sliced is ObjectCreateExpression { Arguments.Count: 1 } spanOfArray && IsVariable(spanOfArray.Arguments.Single(), builder.StoreTarget)))
					return false;
				if (!MatchStart(builder, slice.Arguments.First(), constantIndex, ref usesIndexVariable))
					return false;
				builder.Elements.Add((spanSource, true));
				return true;
			}

			// collection.CopyTo(array, index);
			case ExpressionStatement { Expression: InvocationExpression { Arguments.Count: 2 } collectionCopyTo } when builder.Kind == BuilderKind.Array &&
				collectionCopyTo.GetSymbol() is IMethod { Name: "CopyTo", Parameters.Count: 2 } && IsVariable(collectionCopyTo.Arguments.First(), builderVariable): {
				if (GetReceiver(collectionCopyTo) is not Expression collection || !MatchStart(builder, collectionCopyTo.Arguments.Last(), constantIndex, ref usesIndexVariable))
					return false;
				builder.Elements.Add((collection, true));
				return true;
			}

			// foreach (T item in source) { target[index] = item; index++; }   foreach (T item in source) list.Add(item);
			case ForeachStatement loop when GetForeachVariableName(loop) is not null && IsUsedIn(builder.StoreTarget ?? builderVariable, new[] { loop.EmbeddedStatement }):
				if (!MatchForeachBody(builder, loop))
					return false;
				usesIndexVariable = true;
				builder.Elements.Add((loop.InExpression, true));
				return true;

			// list.Add(value); list.AddRange(source);
			case ExpressionStatement { Expression: InvocationExpression add } when builder.Kind == BuilderKind.List && !builder.HasStores: {
				var addArguments = GetArguments(add);
				if (addArguments.Count != 2 || !IsVariable(addArguments[0], builderVariable) || add.GetSymbol() is not IMethod addMethod)
					return null;
				if (addMethod.Name == "Add" && !addMethod.IsStatic)
					builder.Elements.Add((addArguments[1], false));
				else if (addMethod.Name == "AddRange")
					builder.Elements.Add((addArguments[1], true));
				else
					return null;
				return true;
			}

			// A temporary between the operations (e.g. the span of a spread)
			case VariableDeclarationStatement when MatchDefinition(statement, out var temporary, out var temporaryValue) && temporary != builderVariable && IsSideEffectFreeOrSpan(temporaryValue):
				builder.Temporaries.Add((temporary, statement, temporaryValue, false));
				return true;

			default:
				return null;
			}
		}

		// v++; ++v; v += 1; v = v + 1;
		static bool IsIncrement(Statement statement, ILVariable variable) =>
			statement switch {
				ExpressionStatement { Expression: UnaryOperatorExpression { Operator: UnaryOperatorType.PostIncrement or UnaryOperatorType.Increment } increment } => IsVariable(increment.Expression, variable),
				ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Add, Right: var one } add } => IsVariable(add.Left, variable) && IsConstant(one, 1),
				ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Right: BinaryOperatorExpression { Operator: BinaryOperatorType.Add, Left: var left, Right: var right } } assign } =>
					IsVariable(assign.Left, variable) && IsVariable(left, variable) && right is not null && IsConstant(right, 1),
				_ => false,
			};

		static bool IsListSpan(Builder builder, InvocationExpression asSpan) =>
			IsMethod(asSpan, "System.Runtime.InteropServices.CollectionsMarshal", "AsSpan", out _) && asSpan.Arguments.Count == 1 && IsVariable(asSpan.Arguments.Single(), builder.Variable);

		static bool IsStoreTarget(Builder builder, AstNode? node) =>
			(builder.StoreTarget is not null && IsVariable(node, builder.StoreTarget)) || (builder.DirectSpan && node is InvocationExpression asSpan && IsListSpan(builder, asSpan));

		// The index of a store: a constant (the next one), or the index variable
		static bool MatchStoreIndex(Builder builder, Expression index, ref int constantIndex, ref bool usesIndexVariable) {
			if (index is PrimitiveExpression) {
				if (usesIndexVariable || !IsConstant(index, constantIndex))
					return false;
				constantIndex++;
				return true;
			}
			if (builder.Kind != BuilderKind.InlineArray && MatchIndex(builder, index)) {
				usesIndexVariable = true;
				return true;
			}
			return false;
		}

		static bool MatchIndex(Builder builder, Expression index) {
			if (index is not IdentifierExpression identifier || identifier.GetILVariable() is not ILVariable variable || (builder.IndexVariable is not null && builder.IndexVariable != variable))
				return false;
			builder.IndexVariable = variable;
			return true;
		}

		// Where a spread is copied to: the index variable, or a constant before it's used
		static bool MatchStart(Builder builder, Expression start, int constantIndex, ref bool usesIndexVariable) {
			if (MatchIndex(builder, start)) {
				usesIndexVariable = true;
				return true;
			}
			if (usesIndexVariable || !IsConstant(start, constantIndex))
				return false;
			usesIndexVariable = true;
			return true;
		}

		// The name of a foreach loop's variable
		static string? GetForeachVariableName(ForeachStatement loop) =>
			loop.VariableDesignation is SingleVariableDesignation designation ? designation.Identifier : null;

		// The loop's variable in its body
		static bool IsForeachVariable(AstNode node, ForeachStatement loop) =>
			node is IdentifierExpression identifier && identifier.GetILVariable() is ILVariable variable &&
			(loop.GetILVariable() is ILVariable loopVariable ? loopVariable == variable : variable.Name == GetForeachVariableName(loop));

		/// <summary>
		/// Temporaries kept by a builder (e.g. a count) that nothing uses any more
		/// </summary>
		void RemoveUnusedDefinitions() {
			foreach (var (variable, statement) in keptDefinitions) {
				if (!IsAttached(statement) || !MatchDefinition(statement, out var v, out var value) || v != variable || !IsSideEffectFreeOrSpan(value))
					continue;
				if (references.TryGetValue(variable, out var refs) && refs.Any(a => IsAttached(a) && !a.Ancestors.Contains(statement)))
					continue;
				// A declaration of a variable that's also assigned elsewhere must stay
				if (statement is VariableDeclarationStatement && references.TryGetValue(variable, out var all) && all.Any(IsAttached))
					continue;
				statement.Remove();
			}
		}

		/// <summary>
		/// The compiler stored the arguments evaluated before a collection in temporaries (the code that filled it was in
		/// between): 'int[] xs = new int[1] { 5 }; M(xs, [6, 7]);' becomes 'M(new int[1] { 5 }, [6, 7]);'
		/// </summary>
		void InlineTemporaries() {
			foreach (var statement in statementsWithCollections) {
				if (!IsAttached(statement) || statement.Parent is not BlockStatement block)
					continue;
				while (block.Statements.TakeWhile(a => a != statement).LastOrDefault() is Statement previous &&
					previous is VariableDeclarationStatement && MatchDefinition(previous, out var variable, out var value) && variable.HasGeneratedName &&
					variable.Kind is VariableKind.Local or VariableKind.StackSlot &&
					references.TryGetValue(variable, out var refs) && refs.Where(IsAttached).ToList() is { Count: 1 } uses &&
					uses[0].Ancestors.Contains(statement) && CanMoveTo(uses[0], statement) && !variable.Type.Equals(SpecialType.Dynamic)) {
					previous.Remove();
					uses[0].ReplaceWith(value.Detach());
				}
			}
		}

		/// <summary>
		/// ImmutableArray&lt;T&gt;: 'ImmutableCollectionsMarshal.AsImmutableArray(new int[2] { a, 2 })' (the compiler
		/// creates it like that)
		/// </summary>
		void TryReplaceImmutableArray(InvocationExpression invocation) {
			if (!IsMethod(invocation, "System.Runtime.InteropServices.ImmutableCollectionsMarshal", "AsImmutableArray", out var method) || invocation.Arguments.Count != 1)
				return;
			if (invocation.Arguments.Single() is not ArrayCreateExpression { Initializer: ArrayInitializerExpression initializer, AdditionalArraySpecifiers.Count: 0 } arrayCreation ||
				arrayCreation.Arguments.Count > 1 || (arrayCreation.Arguments.Count == 1 && !IsConstant(arrayCreation.Arguments.Single(), initializer.Elements.Count)))
				return;
			var type = method.ReturnType;
			if (type.TypeArguments.Count != 1)
				return;
			var collection = CreateCollection(initializer.Elements.Select(a => a.Detach()).ToList(), type.TypeArguments[0], type);
			collection.CopyAnnotationsFrom(invocation);
			collection.RemoveAnnotations<ResolveResult>();
			collection.AddAnnotation(new ResolveResult(type));
			CopyILAnnotations(arrayCreation, collection);
			CopyILAnnotations(initializer, collection);
			Replace(invocation, collection, type, t => SameType(t, type), type);
		}

		static bool IsUsedIn(ILVariable variable, IEnumerable<AstNode> nodes) =>
			nodes.Any(n => n.DescendantsAndSelf.OfType<IdentifierExpression>().Any(a => a.GetILVariable() == variable));

		static bool MatchForeachBody(Builder builder, ForeachStatement loop) {
			var body = loop.EmbeddedStatement is BlockStatement block ? block.Statements.ToArray() : new[] { loop.EmbeddedStatement };
			if (builder.Kind == BuilderKind.List && !builder.HasStores) {
				// list.Add(item)
				return body.Length == 1 && body[0] is ExpressionStatement { Expression: InvocationExpression add } && add.GetSymbol() is IMethod { Name: "Add", IsStatic: false } &&
					GetArguments(add) is { Count: 2 } arguments && IsVariable(arguments[0], builder.Variable) && IsForeachVariable(arguments[1], loop);
			}
			// target[index] = item; index++;
			if (!builder.HasStores || body.Length != 2)
				return false;
			if (body[0] is not ExpressionStatement { Expression: AssignmentExpression { Operator: AssignmentOperatorType.Assign, Left: IndexerExpression { Arguments.Count: 1 } store } assignment } ||
				!IsStoreTarget(builder, store.Target) || !IsForeachVariable(assignment.Right, loop) || !MatchIndex(builder, store.Arguments.Single()))
				return false;
			return IsIncrement(body[1], builder.IndexVariable!);
		}

		/// <summary>
		/// The length (array), count (SetCount) or capacity (list) must be the number of elements plus the length of the
		/// spreads, so the collection expression creates the same collection
		/// </summary>
		bool ValidateLength(Builder builder) {
			if (builder.Kind == BuilderKind.InlineArray)
				return true;
			if (builder.Length is null)
				return builder.Kind == BuilderKind.List;
			int singles = builder.Elements.Count(a => !a.spread);
			var spreads = builder.Elements.Where(a => a.spread).Select(a => Underlying(builder, a.expression)).ToList();
			int constant = 0;
			var terms = new List<Expression>();
			if (!CollectTerms(builder, Underlying(builder, builder.Length), ref constant, terms, new HashSet<ILVariable>()))
				return false;
			if (constant != singles || terms.Count != spreads.Count)
				return false;
			// The same sources (in any order)
			var remaining = spreads.ToList();
			foreach (var term in terms) {
				int match = remaining.FindIndex(a => a.IsMatch(term));
				if (match < 0)
					return false;
				remaining.RemoveAt(match);
			}
			return true;
		}

		// 'visiting': the temporaries being expanded. A temporary assigned more than once (e.g. 'num = num + a.Length') has
		// no single value, and temporaries can refer to each other: either would recurse forever (a stack overflow ends
		// dnSpy). Then the length isn't proven and the builder stays as it is.
		bool CollectTerms(Builder builder, Expression expression, ref int constant, List<Expression> terms, HashSet<ILVariable> visiting) {
			switch (expression) {
			case PrimitiveExpression { Value: int value }:
				constant += value;
				return true;
			case BinaryOperatorExpression { Operator: BinaryOperatorType.Add, Left: Expression left, Right: Expression right }:
				return CollectTerms(builder, left, ref constant, terms, visiting) && CollectTerms(builder, right, ref constant, terms, visiting);
			case MemberReferenceExpression { MemberName: "Length" or "Count" } member when member.GetSymbol() is IProperty:
				terms.Add(Underlying(builder, member.Target));
				return true;
			case IdentifierExpression identifier when identifier.GetILVariable() is ILVariable v && builder.Temporaries.FirstOrDefault(a => a.variable == v) is { value: not null, isInternal: false } temporary:
				if (IsDefinedMoreThanOnce(builder, v) || !visiting.Add(v))
					return false;
				bool collected = CollectTerms(builder, temporary.value, ref constant, terms, visiting);
				visiting.Remove(v);
				return collected;
			default:
				return false;
			}
		}

		// What a spread copies: the array of 'new ReadOnlySpan<T>(array)', the list of 'CollectionsMarshal.AsSpan(list)',
		// the value of a temporary
		Expression Underlying(Builder builder, Expression expression) {
			// (Temporaries that refer to each other: stop at the first one seen again)
			var seen = new HashSet<ILVariable>();
			while (true) {
				if (expression is IdentifierExpression identifier && identifier.GetILVariable() is ILVariable v &&
					builder.Temporaries.FirstOrDefault(a => a.variable == v) is { value: not null, isInternal: false } temporary && !IsDefinedMoreThanOnce(builder, v) &&
					seen.Add(v))
					expression = temporary.value;
				else if (UnwrapSpan(expression) is Expression inner)
					expression = inner;
				else
					return expression;
			}
		}

		static bool IsDefinedMoreThanOnce(Builder builder, ILVariable variable) => builder.Temporaries.Count(a => a.variable == variable) > 1;

		static Expression? UnwrapSpan(Expression expression) {
			if (expression is ObjectCreateExpression { Arguments.Count: 1 } creation && creation.GetResolveResult()?.Type is IType spanType &&
				(IsType(spanType, "System.ReadOnlySpan", 1) || IsType(spanType, "System.Span", 1)) &&
				creation.Arguments.Single().GetResolveResult()?.Type is ArrayType { Dimensions: 1 })
				return creation.Arguments.Single();
			if (expression is InvocationExpression { Arguments.Count: 1 } asSpan && IsMethod(asSpan, "System.Runtime.InteropServices.CollectionsMarshal", "AsSpan", out _))
				return asSpan.Arguments.Single();
			return null;
		}

		/// <summary>
		/// Replaces the builder's statements with 'T builder = [...];' (or puts the collection expression where the
		/// builder is used if it's only used once, right after)
		/// </summary>
		bool Rewrite(BlockStatement block, Builder builder, Statement? next) {
			var region = new HashSet<Statement>(builder.Statements);
			bool IsOutside(AstNode node) => IsAttached(node) && !node.Ancestors.Any(a => a is Statement s && region.Contains(s));
			bool IsUsedOutside(ILVariable variable) => references.TryGetValue(variable, out var refs) && refs.Any(IsOutside);

			// Element values: the temporaries the compiler stored them in are replaced with their values if that's
			// their only use. Temporaries used after the builder are kept.
			var elements = builder.Elements.ToList();
			var kept = new HashSet<Statement>();
			var substituted = new List<(int definition, int element)>();
			foreach (var (temporary, statement, value, isInternal) in builder.Temporaries) {
				if (temporary == builder.Variable)
					return false;
				bool usedOutside = IsUsedOutside(temporary);
				if (isInternal) {
					if (usedOutside)
						return false;
					continue;
				}
				int elementIndex = elements.FindIndex(a => IsVariable(a.expression, temporary));
				int uses = elements.Sum(a => a.expression.DescendantsAndSelf.OfType<IdentifierExpression>().Count(i => i.GetILVariable() == temporary));
				if (!usedOutside && !IsDefinedMoreThanOnce(builder, temporary) && uses == 1 && elementIndex >= 0) {
					substituted.Add((builder.Statements.IndexOf(statement), elementIndex));
					elements[elementIndex] = (value, elements[elementIndex].spread);
				}
				else if (!usedOutside && uses == 0) {
					// Index, count: dropped
					if (!IsSideEffectFreeOrSpan(value))
						return false;
				}
				else {
					// Kept, so it must not matter when it's evaluated
					if (!IsSideEffectFreeOrSpan(value))
						return false;
					kept.Add(statement);
					keptDefinitions.Add((temporary, statement));
				}
			}
			if (builder.IndexVariable is not null && IsUsedOutside(builder.IndexVariable))
				return false;
			// The elements keep their order of evaluation: the temporaries were evaluated in order, before the others
			substituted.Sort((a, b) => a.definition.CompareTo(b.definition));
			for (int i = 1; i < substituted.Count; i++) {
				if (substituted[i].element < substituted[i - 1].element)
					return false;
			}
			if (substituted.Count != 0) {
				int lastSubstituted = substituted.Max(a => a.element);
				for (int i = 0; i < lastSubstituted; i++) {
					if (!substituted.Any(a => a.element == i) && !IsSideEffectFree(elements[i].expression))
						return false;
				}
			}
			// The builder: if it's used once, right after, the collection expression goes there (an inline array can't
			// be declared with one)
			var builderUses = references.TryGetValue(builder.Variable, out var builderRefs) ? builderRefs.Where(IsOutside).ToList() : new List<IdentifierExpression>();
			bool inline = builderUses.Count == 1 && next is not null && builderUses[0].Ancestors.Contains(next) && CanMoveTo(builderUses[0], next) &&
				(builder.Variable.HasGeneratedName || builder.Kind == BuilderKind.InlineArray);
			IType collectionType = builder.Variable.Type;
			if (builder.Kind == BuilderKind.InlineArray) {
				if (!inline || !IsSpanConversion(builderUses[0], builder.ElementType, out collectionType))
					return false;
			}

			// Rewrite
			var collectionElements = elements.Select(a => {
				var expression = a.expression.Detach();
				return a.spread ? CreateSpread(UnwrapSpan(expression)?.Detach() ?? expression) : expression;
			}).ToList();
			var collection = CreateCollection(collectionElements, builder.ElementType, collectionType);
			foreach (var statement in builder.Statements) {
				if (kept.Contains(statement))
					continue;
				foreach (var node in statement.DescendantsAndSelf)
					CopyILAnnotations(node, collection);
			}
			var statements = block.Statements.ToList();
			int nextIndex = statements.IndexOf(builder.Statements.Last()) + 1;
			var insertBefore = nextIndex < statements.Count ? statements[nextIndex] : null;
			foreach (var statement in builder.Statements) {
				if (!kept.Contains(statement))
					statement.Remove();
			}
			var elementType = builder.ElementType;
			if (inline) {
				Func<IType, bool> isSameCollection = builder.Kind == BuilderKind.List ?
					type => SameType(type, collectionType) || IsListInterface(type, elementType) :
					type => SameType(type, collectionType);
				Replace(builderUses[0], collection, collectionType, isSameCollection, collectionType);
				statementsWithCollections.Add(next!);
				return true;
			}
			// T builder = [...];
			var anchor = builder.Anchor;
			builder.AnchorValue.ReplaceWith(collection);
			if (insertBefore is not null)
				block.Statements.InsertBefore(insertBefore, anchor);
			else
				block.Statements.Add(anchor);
			return true;
		}

		// The compiler creates a List<T> for these targets
		static bool IsListInterface(IType type, IType elementType) =>
			type.Kind == TypeKind.Interface && type.TypeArguments.Count == 1 && SameType(type.TypeArguments[0], elementType) &&
			type.FullName is "System.Collections.Generic.ICollection" or "System.Collections.Generic.IList";

		static bool IsSideEffectFreeOrSpan(Expression expression) =>
			IsSideEffectFree(expression) || (UnwrapSpan(expression) is Expression inner && IsSideEffectFree(inner));

		static void CopyILAnnotations(AstNode from, AstNode to) {
			foreach (var instruction in from.Annotations.OfType<ILInstruction>())
				to.AddAnnotation(instruction);
		}

		bool IsAttached(AstNode node) => node == root || node.Ancestors.Contains(root);

		// The inline array becomes a span where it's used: 'ReadOnlySpan<int> span = buffer;', 'M(buffer)'
		bool IsSpanConversion(Expression use, IType elementType, out IType spanType) {
			spanType = null!;
			var (type, _) = GetTargetType(use);
			if (type is null || !(IsType(type, "System.ReadOnlySpan", 1) || IsType(type, "System.Span", 1)) || !SameType(type.TypeArguments[0], elementType))
				return false;
			spanType = type;
			return true;
		}
		#endregion

		#region Order of evaluation
		/// <summary>
		/// True if the collection expression can be evaluated where <paramref name="use"/> is instead of before
		/// <paramref name="statement"/>: everything that's evaluated before it in the statement has no side effects, and
		/// it's always evaluated
		/// </summary>
		static bool CanMoveTo(AstNode use, Statement statement) {
			AstNode node = use;
			while (node != statement) {
				var parent = node.Parent;
				switch (parent) {
				case null:
					return false;
				case ParenthesizedExpression:
				case CastExpression:
				case CheckedExpression:
				case UncheckedExpression:
				case UnaryOperatorExpression:
				case MemberReferenceExpression:
				case NamedArgumentExpression:
				case ExpressionStatement:
				case ReturnStatement:
				case YieldReturnStatement:
				case ThrowStatement:
					break;
				case VariableInitializer:
					if (parent.Parent is not VariableDeclarationStatement { Variables.Count: 1 })
						return false;
					break;
				case VariableDeclarationStatement:
					break;
				case InvocationExpression invocation:
					if (node != invocation.Target) {
						if (!IsSideEffectFreeTarget(invocation.Target) || !invocation.Arguments.TakeWhile(a => a != node).All(IsSideEffectFree))
							return false;
					}
					break;
				case ObjectCreateExpression creation:
					if (!creation.Arguments.Contains(node) || !creation.Arguments.TakeWhile(a => a != node).All(IsSideEffectFree))
						return false;
					break;
				case IndexerExpression indexer:
					if (node != indexer.Target && (!(indexer.Target is null || IsSideEffectFree(indexer.Target)) || !indexer.Arguments.TakeWhile(a => a != node).All(IsSideEffectFree)))
						return false;
					break;
				case AssignmentExpression assignment:
					if (node != assignment.Right || !IsSideEffectFreeTarget(assignment.Left))
						return false;
					break;
				case BinaryOperatorExpression binary:
					if (binary.Operator is BinaryOperatorType.ConditionalAnd or BinaryOperatorType.ConditionalOr or BinaryOperatorType.NullCoalescing && node == binary.Right)
						return false;
					if (node == binary.Right && binary.Left is not null && !IsSideEffectFree(binary.Left))
						return false;
					break;
				case ArrayInitializerExpression elements:
					if (!elements.Elements.TakeWhile(a => a != node).All(IsSideEffectFree))
						return false;
					if (elements.Parent is ArrayCreateExpression arrayCreation && !arrayCreation.Arguments.All(IsSideEffectFree))
						return false;
					break;
				case ArrayCreateExpression:
					break;
				case ConditionalExpression conditional:
					if (node != conditional.Condition)
						return false;
					break;
				default:
					return false;
				}
				node = parent;
			}
			return true;
		}

		// The target of a call or an assignment: evaluating it has no side effects
		static bool IsSideEffectFreeTarget(Expression target) =>
			target switch {
				IdentifierExpression => true,
				MemberReferenceExpression member => member.Target is TypeReferenceExpression || IsSideEffectFree(member.Target),
				IndexerExpression indexer => (indexer.Target is null || IsSideEffectFree(indexer.Target)) && indexer.Arguments.All(IsSideEffectFree),
				_ => false,
			};

		static bool IsSideEffectFree(Expression expression) =>
			expression switch {
				PrimitiveExpression or NullReferenceExpression or ThisReferenceExpression or BaseReferenceExpression or TypeReferenceExpression or
				TypeOfExpression or DefaultValueExpression or SizeOfExpression or LambdaExpression or AnonymousMethodExpression => true,
				IdentifierExpression identifier => identifier.GetILVariable() is not null || identifier.GetSymbol() is IField or IMethod or IType,
				MemberReferenceExpression member => member.GetSymbol() is IField or IType && IsSideEffectFree(member.Target),
				ParenthesizedExpression parenthesized => IsSideEffectFree(parenthesized.Expression),
				// An upcast can't throw, e.g. '(IFormatProvider?)culture' (an argument the compiler evaluated before an
				// inline array of params)
				CastExpression cast => cast.GetResolveResult() is ConversionResolveResult { Conversion: { IsImplicit: true } conversion } &&
					(conversion.IsIdentityConversion || conversion.IsReferenceConversion) && IsSideEffectFree(cast.Expression),
				BinaryOperatorExpression { Operator: BinaryOperatorType.Add or BinaryOperatorType.Subtract or BinaryOperatorType.Multiply, Left: Expression left, Right: Expression right } binary =>
					binary.GetResolveResult() is not OperatorResolveResult { UserDefinedOperatorMethod: not null } && IsSideEffectFree(left) && IsSideEffectFree(right),
				_ => false,
			};
		#endregion
	}
}
