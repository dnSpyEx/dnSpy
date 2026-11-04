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
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Resolver;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.IL;
using ICSharpCode.Decompiler.Semantics;
using ICSharpCode.Decompiler.TypeSystem;
using Attribute = ICSharpCode.Decompiler.CSharp.Syntax.Attribute;
using IType = ICSharpCode.Decompiler.TypeSystem.IType;
using IMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;

namespace dnSpy.Decompiler.ILSpy.Core {
	// The code that dnSpy's edit code commands (Edit Method, Edit Class, Add Class Members, Edit Assembly
	// Attributes) compile, and the partial types of Export to Project (UpstreamCSharpLanguage.Project.cs)
	sealed partial class UpstreamCSharpLanguage {
		public override bool CanDecompile(DecompilationType decompilationType) {
			switch (decompilationType) {
			case DecompilationType.TypeMethods:
			case DecompilationType.AssemblyInfo:
			case DecompilationType.PartialType:
				return true;
			}
			return false;
		}

		public override void Decompile(DecompilationType decompilationType, object data) {
			switch (decompilationType) {
			case DecompilationType.TypeMethods:
				DecompileTypeMethods((DecompileTypeMethods)data);
				return;
			case DecompilationType.AssemblyInfo:
				DecompileAssemblyInfo((DecompileAssemblyInfo)data);
				return;
			case DecompilationType.PartialType:
				DecompilePartialType((DecompilePartialType)data);
				return;
			}
			base.Decompile(decompilationType, data);
		}

		/// <summary>
		/// Min size of the method bodies that neither document needs before they're left out of the decompiled image
		/// (see <see cref="GetUnusedBodies"/>). Below it, creating the image costs about as much as it saves. Tests set it
		/// to 0.
		/// </summary>
		internal int EditUnusedBodiesMinSize { get; set; } = 16 * 1024;

		/// <summary>
		/// The type with only some of its members (Edit Method: the main document has the edited method, a second
		/// document has all the other members so it compiles; Add Class Members: the main document has the types
		/// without members), or the whole type (Edit Class). Both documents are cut from a decompilation of the whole
		/// type, so they agree on what the compiler generates (auto properties, field initializers, record members...).
		/// </summary>
		void DecompileTypeMethods(DecompileTypeMethods info) {
			var type = info.Type;
			var selection = new EditSelection(info);
			var unused = info.ShowAll && !info.DecompileHidden ? null : GetUnusedBodies(type, selection);
			bool withoutUnusedBodies = false;
			Decompile(type.Module, GetMethods(type), info.Output, info.Context,
				(decompiler, handle) => decompiler.DecompileTypes(new[] { (TypeDefinitionHandle)handle }), type,
				configureSettings: ConfigureEditSettings,
				configure: (decompiler, bridged) => {
					decompiler.AstTransforms.Add(new RemoveReservedAttributesTransform());
					if (!info.ShowAll || info.DecompileHidden) {
						decompiler.AstTransforms.Add(new EditCodeTransform(selection, cache, bridged, info.DecompileHidden, withoutUnusedBodies ? unused : null));
					}
				},
				writeHeader: UsesNullableMetadata(type) ? WriteNullableAnnotationsDirective : null,
				createImage: unused is null || unused.Methods.Count == 0 ? null : bridged => {
					var image = ImageWithoutMethodBodies.TryCreate(cache, bridged, unused.Methods, EditUnusedBodiesMinSize);
					if (image is not null) {
						withoutUnusedBodies = true;
						Interlocked.Increment(ref imagesWithoutUnusedBodies);
					}
					return image;
				});
		}

		sealed class UnusedBodies {
			public HashSet<MethodDef> Methods { get; } = new HashSet<MethodDef>();
			/// <summary>
			/// The compiler-generated types of the async methods and iterators in <see cref="Methods"/>. Their methods are
			/// in <see cref="Methods"/>. The state machine attribute of the method that isn't decompiled references the
			/// type, so upstream shows it as a type: it's removed.
			/// </summary>
			public HashSet<TypeDef> StateMachineTypes { get; } = new HashSet<TypeDef>();
		}

		/// <summary>
		/// Tests: number of decompilations that used an image without the unused method bodies
		/// </summary>
		internal int ImagesWithoutUnusedBodies => Volatile.Read(ref imagesWithoutUnusedBodies);
		int imagesWithoutUnusedBodies;

		/// <summary>
		/// Methods whose code neither document shows: the main document doesn't have them and the second one only needs
		/// their signatures ('throw null;' bodies). Upstream can only decompile a whole type, so in a big type most of the
		/// time would go to code that's thrown away. Code that's needed to decompile the type or the shown members stays:
		/// constructors (field initializers, primary constructors), accessors (auto properties, the field keyword,
		/// events), compiler-generated methods and types (record members, lambdas, iterators, async methods), interface
		/// methods (without a body they'd be abstract).
		/// </summary>
		static UnusedBodies? GetUnusedBodies(TypeDef type, EditSelection selection) {
			// An edited constructor stores primary constructor parameters only if other members use them, so their code
			// is needed (see EditCodeTransform.UsesPrimaryConstructorParameters)
			foreach (var method in selection.Methods) {
				if (method.IsInstanceConstructor && method.DeclaringType is TypeDef declType && declType.Fields.Any(IsPrimaryConstructorParameterField))
					return null;
			}
			var result = new UnusedBodies();
			AddUnusedBodies(type, selection, result);
			return result;
		}

		static void AddUnusedBodies(TypeDef type, EditSelection selection, UnusedBodies result) {
			if (IsCompilerGenerated(type) || type.IsInterface)
				return;
			foreach (var method in type.Methods) {
				if (selection.Shown.Contains(method) || method.IsConstructor || method.SemanticsAttributes != dnlib.DotNet.MethodSemanticsAttributes.None ||
					UTF8String.ToSystemStringOrEmpty(method.Name).StartsWith("<", StringComparison.Ordinal) || IsCompilerGenerated(method))
					continue;
				var stateMachine = GetStateMachineType(method);
				// Code that upstream only hides when it decompiles the method that uses it: a lambda's closure class or
				// method, a local function, a state machine it can't find. Without the method's body upstream shows it
				// (as types and members with the compiler's names, which don't compile), so the method is decompiled.
				if (UsesCompilerGeneratedCode(method, stateMachine))
					continue;
				result.Methods.Add(method);
				if (stateMachine is not null && result.StateMachineTypes.Add(stateMachine))
					AddAllMethods(stateMachine, result.Methods);
			}
			foreach (var nested in type.NestedTypes)
				AddUnusedBodies(nested, selection, result);
		}

		/// <summary>
		/// Does the method's code (and its state machine's, which is left out with it) use compiler-generated nested types
		/// other than that state machine, or compiler-generated methods ('&lt;M&gt;b__0_0', '&lt;M&gt;g__Local|0_0')?
		/// Top-level ones (array data in '&lt;PrivateImplementationDetails&gt;') aren't part of the decompiled type.
		/// </summary>
		static bool UsesCompilerGeneratedCode(MethodDef method, TypeDef? stateMachine) {
			bool IsAllowed(TypeDef t) {
				for (var d = t; d is not null; d = d.DeclaringType) {
					if (d == stateMachine)
						return true;
				}
				return false;
			}
			bool IsCompilerGeneratedNested(TypeDef? t) {
				if (t is null || IsAllowed(t))
					return false;
				for (var d = t; d is not null && d.DeclaringType is not null; d = d.DeclaringType) {
					if (IsCompilerGenerated(d))
						return true;
				}
				return false;
			}
			bool Uses(MethodDef body) {
				if (!body.HasBody)
					return false;
				foreach (var instr in body.Body.Instructions) {
					switch (instr.Operand) {
					case ITypeDefOrRef typeRef when IsCompilerGeneratedNested(typeRef.ResolveTypeDef()):
						return true;
					case dnlib.DotNet.IMethod m when m.IsMethod && m.ResolveMethodDef() is MethodDef md && (IsCompilerGeneratedNested(md.DeclaringType) ||
						(md.DeclaringType is TypeDef mdType && !IsAllowed(mdType) && UTF8String.ToSystemStringOrEmpty(md.Name).StartsWith("<", StringComparison.Ordinal))):
						return true;
					case dnlib.DotNet.IField f when f.ResolveFieldDef() is FieldDef fd && IsCompilerGeneratedNested(fd.DeclaringType):
						return true;
					}
				}
				return false;
			}
			if (Uses(method))
				return true;
			if (stateMachine is not null) {
				var methods = new HashSet<MethodDef>();
				AddAllMethods(stateMachine, methods);
				return methods.Any(Uses);
			}
			return false;
		}

		static readonly string[] stateMachineAttributes = {
			"System.Runtime.CompilerServices.AsyncIteratorStateMachineAttribute",
			"System.Runtime.CompilerServices.AsyncStateMachineAttribute",
			"System.Runtime.CompilerServices.IteratorStateMachineAttribute",
		};

		/// <summary>
		/// The compiler-generated type of an async method or iterator (a type nested in the method's type)
		/// </summary>
		static TypeDef? GetStateMachineType(MethodDef method) {
			foreach (var ca in method.CustomAttributes) {
				if (Array.IndexOf(stateMachineAttributes, ca.TypeFullName) < 0 || ca.ConstructorArguments.Count != 1)
					continue;
				if (ca.ConstructorArguments[0].Value is TypeSig sig && sig.ToTypeDefOrRef().ResolveTypeDef() is TypeDef stateMachine &&
					stateMachine.DeclaringType == method.DeclaringType && IsCompilerGenerated(stateMachine))
					return stateMachine;
			}
			return null;
		}

		static void AddAllMethods(TypeDef type, HashSet<MethodDef> methods) {
			methods.UnionWith(type.Methods);
			foreach (var nested in type.NestedTypes)
				AddAllMethods(nested, methods);
		}

		static bool IsCompilerGenerated(IHasCustomAttribute member) =>
			member.CustomAttributes.IsDefined("System.Runtime.CompilerServices.CompilerGeneratedAttribute");

		static bool IsCompilerGenerated(TypeDef type) =>
			UTF8String.ToSystemStringOrEmpty(type.Name).StartsWith("<", StringComparison.Ordinal) || IsCompilerGenerated((IHasCustomAttribute)type);

		// The compiler's fields for captured primary constructor parameters: '<name>P'
		static bool IsPrimaryConstructorParameterField(FieldDef field) {
			var name = UTF8String.ToSystemStringOrEmpty(field.Name);
			return name.StartsWith("<", StringComparison.Ordinal) && name.EndsWith(">P", StringComparison.Ordinal) && IsCompilerGenerated(field);
		}

		/// <summary>
		/// The code is compiled: no settings that make code that doesn't compile. The nullable annotations are shown so
		/// the compiler can recreate the nullable metadata (see <see cref="WriteNullableAnnotationsDirective"/>).
		/// </summary>
		static void ConfigureEditSettings(DecompilerSettings settings) {
			settings.DecompileMemberBodies = true;
			settings.NullableReferenceTypes = true;
		}

		/// <summary>
		/// dnSpy compiles edited code without a nullable context, so the compiler wouldn't emit the nullable metadata
		/// ([NullableContext], [Nullable]) of the code it compiles. In the nullable annotations context it recreates it
		/// from the decompiled annotations ('string?'), without nullable warnings. (dnSpy keeps the original metadata
		/// where it can, see ModuleImporter.)
		/// </summary>
		static void WriteNullableAnnotationsDirective(IDecompilerOutput output) {
			output.Write("#nullable", BoxedTextColor.PreprocessorKeyword);
			output.Write(" ", BoxedTextColor.Text);
			output.Write("enable annotations", BoxedTextColor.PreprocessorText);
			output.WriteLine();
		}

		/// <summary>
		/// Does the type (or a nested type or member) have nullable metadata?
		/// </summary>
		static bool UsesNullableMetadata(TypeDef type) {
			foreach (var t in type.GetTypes().Prepend(type)) {
				if (HasNullableMetadata(t))
					return true;
				foreach (var m in t.Methods) {
					if (HasNullableMetadata(m))
						return true;
					foreach (var p in m.ParamDefs) {
						if (HasNullableMetadata(p))
							return true;
					}
				}
				foreach (var f in t.Fields) {
					if (HasNullableMetadata(f))
						return true;
				}
				foreach (var p in t.Properties) {
					if (HasNullableMetadata(p))
						return true;
				}
				foreach (var e in t.Events) {
					if (HasNullableMetadata(e))
						return true;
				}
			}
			return false;
		}

		static bool HasNullableMetadata(IHasCustomAttribute member) {
			foreach (var ca in member.CustomAttributes) {
				var name = ca.TypeFullName;
				if (name == "System.Runtime.CompilerServices.NullableAttribute" || name == "System.Runtime.CompilerServices.NullableContextAttribute")
					return true;
			}
			return false;
		}

		/// <summary>
		/// The assembly and module attributes (Edit Assembly Attributes). For a project's AssemblyInfo.cs, without the
		/// attributes the compiler or MSBuild adds.
		/// </summary>
		void DecompileAssemblyInfo(DecompileAssemblyInfo info) {
			Decompile(info.Module, Array.Empty<MethodDef>(), info.Output, info.Context,
				(decompiler, handle) => decompiler.DecompileModuleAndAssemblyAttributes(), null,
				configureSettings: ConfigureEditSettings,
				configure: (decompiler, bridged) => decompiler.AstTransforms.Add(new AssemblyInfoTransform(info.KeepAllAttributes)));
		}

		/// <summary>
		/// The members of the main document (like dnSpy's previous C# decompiler)
		/// </summary>
		sealed class EditSelection {
			/// <summary>Members in the main document (a property or event with all its accessors)</summary>
			public HashSet<IMemberDef> Shown { get; } = new HashSet<IMemberDef>();
			/// <summary>Types in both documents (partial)</summary>
			public HashSet<TypeDef> PartialTypes { get; } = new HashSet<TypeDef>();
			/// <summary>The edited methods</summary>
			public HashSet<MethodDef> Methods { get; }

			public EditSelection(DecompileTypeMethods info) {
				Methods = info.Methods;
				foreach (var method in info.Methods) {
					var declType = method.DeclaringType;
					var property = declType.Properties.FirstOrDefault(a => a.GetMethods.Contains(method) || a.SetMethods.Contains(method) || a.OtherMethods.Contains(method));
					var ev = property is null ? declType.Events.FirstOrDefault(a => a.AddMethod == method || a.RemoveMethod == method || a.InvokeMethod == method || a.OtherMethods.Contains(method)) : null;
					if (property is not null) {
						Shown.Add(property);
						foreach (var m in property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods))
							Shown.Add(m);
					}
					else if (ev is not null) {
						Shown.Add(ev);
						foreach (var m in new[] { ev.AddMethod, ev.RemoveMethod, ev.InvokeMethod }.Concat(ev.OtherMethods)) {
							if (m is not null)
								Shown.Add(m);
						}
					}
					else
						Shown.Add(method);
				}
				foreach (var type in info.Types) {
					Shown.Add(type);
					if (!type.IsEnum)
						PartialTypes.Add(type);
				}
				foreach (var def in Shown.ToArray()) {
					for (var t = def.DeclaringType; t is not null; t = t.DeclaringType)
						PartialTypes.Add(t);
				}
				// An enum can't be partial: the main document has all of it (added after its declaring types were
				// made partial, so it isn't one of them)
				foreach (var type in info.Types) {
					if (type.IsEnum) {
						foreach (var f in type.Fields)
							Shown.Add(f);
					}
				}
			}
		}

		/// <summary>
		/// Keeps the members of one of the documents. Types in both documents are partial and only the main document
		/// declares their base types, attributes and primary constructor. The second document's members have stub
		/// bodies ('throw null;'): only the edited members are imported, the others just have to compile.
		/// </summary>
		sealed class EditCodeTransform : IAstTransform {
			readonly EditSelection selection;
			readonly ModuleBridgeCache cache;
			readonly BridgedModule bridged;
			readonly bool hidden;
			readonly UnusedBodies? unusedBodies;
			readonly HashSet<ISymbol> removedSymbols = new HashSet<ISymbol>();

			/// <param name="unusedBodies">The methods without bodies in the decompiled image, or null</param>
			public EditCodeTransform(EditSelection selection, ModuleBridgeCache cache, BridgedModule bridged, bool hidden, UnusedBodies? unusedBodies) {
				this.selection = selection;
				this.cache = cache;
				this.bridged = bridged;
				this.hidden = hidden;
				this.unusedBodies = unusedBodies;
			}

			IMemberDef? ToDnlib(AstNode node) => node.GetSymbol() is IEntity entity ? cache.ToDnlib(entity) as IMemberDef : null;

			/// <summary>
			/// Is the member in the main document? A member of an extension block ('extension(string s) { ... }')
			/// is a metadata-only method of a nested type: its code is in the implementation method (the one that's
			/// edited).
			/// </summary>
			bool IsShown(EntityDeclaration entity) {
				if (entity.Parent is ExtensionDeclaration) {
					var accessors = entity.GetSymbol() switch {
						IProperty p => new[] { p.Getter, p.Setter },
						IMethod m => new[] { m },
						_ => Array.Empty<IMethod?>(),
					};
					foreach (var accessor in accessors) {
						if (accessor is null)
							continue;
						var info = accessor.ResolveExtensionInfo()?.InfoOfExtensionMember((IMethod)accessor.MemberDefinition);
						if (info is ExtensionMemberInfo memberInfo && cache.ToDnlib(memberInfo.ImplementationMethod) is MethodDef impl && selection.Shown.Contains(impl))
							return true;
					}
					return false;
				}
				return ToDnlib(entity) is IMemberDef def && selection.Shown.Contains(def);
			}

			public void Run(AstNode rootNode, TransformContext context) {
				Visit(rootNode);
				foreach (var comment in rootNode.Descendants.OfType<Comment>().ToArray()) {
					if (comment.GetSymbol() is ISymbol symbol && removedSymbols.Contains(symbol))
						comment.Remove();
				}
				if (!hidden)
					AddMissingConstructors(rootNode, context);
			}

			void Visit(AstNode node) {
				foreach (var child in node.Children.ToArray()) {
					if (child is not EntityDeclaration entity) {
						if (child is NamespaceDeclaration || child is SyntaxTree)
							Visit(child);
						continue;
					}
					var def = ToDnlib(entity);
					if (def is TypeDef removedType && unusedBodies?.StateMachineTypes.Contains(removedType) == true) {
						Remove(entity);
						continue;
					}
					if (def is TypeDef type && entity is TypeDeclaration typeDecl && selection.PartialTypes.Contains(type)) {
						MakePartial(typeDecl);
						Visit(typeDecl);
						continue;
					}
					if (entity is ExtensionDeclaration extension) {
						// Several blocks can extend the same type: each document has the blocks with its members
						foreach (var member in extension.Members.ToArray()) {
							if (IsShown(member) == hidden)
								Remove(member);
							else if (hidden)
								AddStubBodies(member);
						}
						if (extension.Members.Count == 0)
							Remove(extension);
						continue;
					}
					bool inMain = IsShown(entity);
					if (inMain == hidden) {
						Remove(entity);
						continue;
					}
					if (hidden)
						AddStubBodies(entity);
				}
			}

			void Remove(AstNode node) {
				if (node.GetSymbol() is ISymbol symbol)
					removedSymbols.Add(symbol);
				node.Remove();
			}

			void MakePartial(TypeDeclaration typeDecl) {
				if (typeDecl.ClassType != ClassType.Enum)
					typeDecl.Modifiers |= Modifiers.Partial;
				if (hidden) {
					// The main document has them
					typeDecl.BaseTypes.Clear();
					typeDecl.Attributes.Clear();
					// (The compiler merges the parts' type parameter attributes: CS0579 Duplicate 'DynamicallyAccessedMembers')
					foreach (var typeParameter in typeDecl.TypeParameters)
						typeParameter.Attributes.Clear();
					typeDecl.PrimaryConstructorParameters.Clear();
					typeDecl.HasPrimaryConstructor = false;
				}
			}

			void AddStubBodies(AstNode node) {
				// The state machines of the methods without code are also in nested types that are shown whole
				if (unusedBodies is not null) {
					foreach (var nestedType in node.Descendants.OfType<TypeDeclaration>().ToArray()) {
						if (ToDnlib(nestedType) is TypeDef t && unusedBodies.StateMachineTypes.Contains(t))
							Remove(nestedType);
					}
				}
				foreach (var n in node.DescendantsAndSelf.ToArray()) {
					if (n is EntityDeclaration entity) {
						RemoveStateMachineAttributes(entity);
						// Upstream shows a method without a body in the image as 'extern'
						if (unusedBodies is not null && (entity.Modifiers & Modifiers.Extern) != 0 && ToDnlib(entity) is MethodDef md && unusedBodies.Methods.Contains(md)) {
							entity.Modifiers &= ~Modifiers.Extern;
							switch (entity) {
							case MethodDeclaration m when m.Body is null:
								m.Body = CreateStubBody();
								break;
							case OperatorDeclaration o when o.Body is null:
								o.Body = CreateStubBody();
								break;
							case DestructorDeclaration d when d.Body is null:
								d.Body = CreateStubBody();
								break;
							}
						}
					}
					if (n is EntityDeclaration member && UsesPrimaryConstructorParameters(member))
						continue;
					switch (n) {
					case MethodDeclaration { Body: not null } m:
						m.Body = CreateStubBody();
						// An async iterator must have a 'yield', and the stub isn't async
						m.Modifiers &= ~Modifiers.Async;
						break;
					case ConstructorDeclaration { Body: not null } c:
						c.Body = CreateStubBody();
						break;
					case DestructorDeclaration { Body: not null } d:
						d.Body = CreateStubBody();
						break;
					case OperatorDeclaration { Body: not null } o:
						o.Body = CreateStubBody();
						break;
					case Accessor { Body: not null } a:
						a.Body = CreateStubBody();
						break;
					// An auto property whose getter was removed (by the IL trimmer: .NET's System.Text.Json and
					// System.Private.Xml) is 'bool IsConfigured { set; }', which doesn't compile (CS8051)
					case PropertyDeclaration { Getter: null, Setter: { Body: null } setter }:
						setter.Body = CreateStubBody();
						break;
					case PropertyDeclaration { ExpressionBody: not null } p:
						p.ExpressionBody = new ThrowExpression(new NullReferenceExpression());
						break;
					case IndexerDeclaration { ExpressionBody: not null } i:
						i.ExpressionBody = new ThrowExpression(new NullReferenceExpression());
						break;
					}
				}
			}

			static BlockStatement CreateStubBody() => new BlockStatement { new ThrowStatement(new NullReferenceExpression()) };

			/// <summary>
			/// Upstream removes them from the async methods and iterators it decompiles. A method whose code wasn't
			/// decompiled (see GetUnusedBodies) still has them, and they reference compiler-generated types.
			/// </summary>
			static void RemoveStateMachineAttributes(EntityDeclaration member) {
				foreach (var section in member.Attributes.ToArray()) {
					foreach (var attribute in section.Attributes.ToArray()) {
						if (attribute.Type.GetSymbol() is IType type && Array.IndexOf(stateMachineAttributes, type.ReflectionName) >= 0)
							attribute.Remove();
					}
					if (section.Attributes.Count == 0)
						section.Remove();
				}
			}

			/// <summary>
			/// Does the member use a parameter of the type's primary constructor? Then its code is kept: the compiler
			/// stores such parameters in fields (captures them) only if a member uses them, so without it the edited
			/// constructor wouldn't store them.
			/// </summary>
			static bool UsesPrimaryConstructorParameters(EntityDeclaration member) {
				if (member is TypeDeclaration || member.GetSymbol() is not IMember symbol || symbol.DeclaringTypeDefinition is not ITypeDefinition declType)
					return false;
				foreach (var identifier in member.Descendants.OfType<IdentifierExpression>()) {
					if (identifier.Annotation<ILVariableResolveResult>()?.Variable is ILVariable { Kind: VariableKind.Parameter } variable &&
						variable.Function?.Method is IMethod { IsConstructor: true, IsStatic: false } ctor &&
						ctor.DeclaringTypeDefinition?.Equals(declType) == true && !ctor.Equals(symbol))
						return true;
				}
				return false;
			}

			/// <summary>
			/// Upstream removes constructors that the compiler adds by itself (an empty default constructor, a static
			/// constructor whose code became field initializers). Add the edited one so it can be edited.
			/// </summary>
			void AddMissingConstructors(AstNode rootNode, TransformContext context) {
				var declared = new HashSet<IMemberDef>();
				foreach (var entity in rootNode.Descendants.OfType<EntityDeclaration>()) {
					if (ToDnlib(entity) is IMemberDef def)
						declared.Add(def);
				}
				foreach (var method in selection.Methods) {
					if (!method.IsConstructor || method.MethodSig?.Params.Count != 0 || declared.Contains(method))
						continue;
					var typeDecl = rootNode.Descendants.OfType<TypeDeclaration>().FirstOrDefault(a => ToDnlib(a) == method.DeclaringType);
					if (typeDecl is null || typeDecl.HasPrimaryConstructor)
						continue;
					int token = bridged.Tokens.GetToken(method);
					if (token == 0 || context.TypeSystem.MainModule.ResolveEntity(MetadataTokens.EntityHandle(token)) is not IMethod upstreamMethod)
						continue;
					var ctor = new ConstructorDeclaration {
						Name = typeDecl.Name,
						Body = new BlockStatement(),
					};
					ctor.Modifiers = method.IsStatic ? Modifiers.Static : GetAccessibility(method);
					ctor.AddAnnotation(new MemberResolveResult(null, upstreamMethod));
					var lastCtor = typeDecl.Members.LastOrDefault(a => a is ConstructorDeclaration || a is FieldDeclaration);
					if (lastCtor is not null)
						typeDecl.Members.InsertAfter(lastCtor, ctor);
					else
						typeDecl.Members.InsertBefore(typeDecl.Members.FirstOrDefault()!, ctor);
				}
			}

			static Modifiers GetAccessibility(MethodDef method) {
				switch (method.Access) {
				case MethodAttributes.Public:		return Modifiers.Public;
				case MethodAttributes.Family:		return Modifiers.Protected;
				case MethodAttributes.Assembly:		return Modifiers.Internal;
				case MethodAttributes.FamORAssem:	return Modifiers.Protected | Modifiers.Internal;
				case MethodAttributes.FamANDAssem:	return Modifiers.Private | Modifiers.Protected;
				default:							return Modifiers.Private;
				}
			}
		}

		/// <summary>
		/// Removes the assembly and module attributes that only the compiler may add (they don't compile), and for a
		/// project's AssemblyInfo.cs also the ones the compiler or MSBuild adds (like dnSpy's previous C# decompiler)
		/// </summary>
		sealed class AssemblyInfoTransform : IAstTransform {
			static readonly HashSet<string> addedByBuild = new HashSet<string>(StringComparer.Ordinal) {
				"System.Runtime.Versioning.TargetFrameworkAttribute",
				"System.Security.UnverifiableCodeAttribute",
				"System.Runtime.CompilerServices.CompilationRelaxationsAttribute",
				"System.Runtime.CompilerServices.RuntimeCompatibilityAttribute",
				"System.Diagnostics.DebuggableAttribute",
			};

			readonly bool keepAllAttributes;

			public AssemblyInfoTransform(bool keepAllAttributes) => this.keepAllAttributes = keepAllAttributes;

			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var section in rootNode.Descendants.OfType<AttributeSection>().ToArray()) {
					foreach (var attribute in section.Attributes.ToArray()) {
						if (IsRemoved(attribute.Type.GetSymbol() as IType))
							attribute.Remove();
					}
					if (section.Attributes.Count == 0)
						section.Remove();
				}
			}

			bool IsRemoved(IType? type) {
				if (type is null)
					return false;
				if (CompilerReservedAttributes.IsReserved(type.ReflectionName))
					return true;
				if (keepAllAttributes)
					return false;
				if (addedByBuild.Contains(type.ReflectionName))
					return true;
				return type.GetAllBaseTypeDefinitions().Any(a => a.FullName == "System.Security.Permissions.SecurityAttribute");
			}
		}

		/// <summary>
		/// Removes the attributes that the compiler rejects in source code from types and members. Upstream turns them into
		/// syntax and removes them, but not all of them (11.2 shows the [CompilerFeatureRequired] of C# 14 compound
		/// assignment operators). The compiler adds them again. A no-op if there are none.
		/// </summary>
		sealed class RemoveReservedAttributesTransform : IAstTransform {
			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var section in rootNode.Descendants.OfType<AttributeSection>().ToArray()) {
					// (Assembly and module attributes: AssemblyInfoTransform)
					if (section.AttributeTarget == "assembly" || section.AttributeTarget == "module")
						continue;
					foreach (var attribute in section.Attributes.ToArray()) {
						if (attribute.Type.GetSymbol() is IType type && CompilerReservedAttributes.IsReserved(type.ReflectionName))
							attribute.Remove();
					}
					if (section.Attributes.Count == 0)
						section.Remove();
				}
			}
		}

		/// <summary>
		/// Attributes the C# compiler emits for language features and rejects in source code
		/// </summary>
		static class CompilerReservedAttributes {
			static readonly HashSet<string> names = new HashSet<string>(StringComparer.Ordinal) {
				"System.Runtime.CompilerServices.CompilerFeatureRequiredAttribute",
				"System.Runtime.CompilerServices.DynamicAttribute",
				"System.Runtime.CompilerServices.ExtensionAttribute",
				"System.Runtime.CompilerServices.ExtensionMarkerAttribute",
				"System.Runtime.CompilerServices.IsByRefLikeAttribute",
				"System.Runtime.CompilerServices.IsReadOnlyAttribute",
				"System.Runtime.CompilerServices.IsUnmanagedAttribute",
				"System.Runtime.CompilerServices.NativeIntegerAttribute",
				"System.Runtime.CompilerServices.NullableAttribute",
				"System.Runtime.CompilerServices.NullableContextAttribute",
				"System.Runtime.CompilerServices.NullablePublicOnlyAttribute",
				"System.Runtime.CompilerServices.ParamCollectionAttribute",
				"System.Runtime.CompilerServices.RefSafetyRulesAttribute",
				"System.Runtime.CompilerServices.RequiredMemberAttribute",
				"System.Runtime.CompilerServices.RequiresLocationAttribute",
				"System.Runtime.CompilerServices.ScopedRefAttribute",
				"System.Runtime.CompilerServices.TupleElementNamesAttribute",
			};

			public static bool IsReserved(string reflectionName) => names.Contains(reflectionName);
		}
	}
}
