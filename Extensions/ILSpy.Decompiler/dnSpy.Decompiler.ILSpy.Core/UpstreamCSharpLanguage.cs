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
using System.Runtime.CompilerServices;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.Text;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using dnSpy.Decompiler.ILSpy.Core.DebugInfo;
using dnSpy.Decompiler.ILSpy.Core.Output;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Core.Text;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using UpstreamCSharpDecompilerEngine = ICSharpCode.Decompiler.CSharp.CSharpDecompiler;
using UpstreamField = ICSharpCode.Decompiler.TypeSystem.IField;
using UpstreamMethod = ICSharpCode.Decompiler.TypeSystem.IMethod;

namespace dnSpy.Decompiler.ILSpy.Core {
	/// <summary>
	/// Creates the decompiler in dnSpy.Console (dnSpy creates it with an <see cref="IDecompilerCreator"/>)
	/// </summary>
	public sealed class UpstreamDecompilerProvider : IDecompilerProvider {
		// Keep the default ctor. It's used by dnSpy.Console.exe
		public UpstreamDecompilerProvider() { }

		public IEnumerable<IDecompiler> Create() {
			var csharp = new UpstreamCSharpLanguage(new UpstreamDecompilerSettings());
			yield return csharp;
#if DEBUG
			foreach (var ilast in ILAst.UpstreamILAstLanguage.Create(csharp))
				yield return ilast;
#endif
		}
	}

	/// <summary>
	/// C# decompiler using the unmodified upstream ILSpy engine (ICSharpCode.Decompiler NuGet package)
	/// </summary>
	sealed partial class UpstreamCSharpLanguage : DecompilerBase {
		/// <summary>
		/// dnSpy's C# language (the GUID of the decompiler this one replaced, so saved tabs and settings keep it)
		/// </summary>
		public static readonly Guid LanguageGuid = DecompilerConstants.LANGUAGE_CSHARP_ILSPY;
		public const string ContentTypeName = ContentTypesInternal.CSharpILSpy;

		readonly UpstreamDecompilerSettings langSettings;
		readonly ModuleEditTracker editTracker;
		readonly ModuleBridgeCache cache;
		readonly MemberVisibility memberVisibility = new MemberVisibility();
		readonly TypeSystemCache typeSystems;

		// The ILAst views (debug builds) share the images and type systems
		internal ModuleBridgeCache Cache => cache;
		internal TypeSystemCache TypeSystems => typeSystems;
		internal UpstreamDecompilerSettings LanguageSettings => langSettings;

		/// <summary>
		/// XML documentation (tests replace it)
		/// </summary>
		internal IDocumentationProvider DocumentationProvider { get; set; }

		public UpstreamCSharpLanguage(UpstreamDecompilerSettings langSettings) : this(langSettings, null) { }

		/// <param name="langSettings">Settings</param>
		/// <param name="onBridging">Tests: called when a module is about to be bridged</param>
		internal UpstreamCSharpLanguage(UpstreamDecompilerSettings langSettings, Action<ModuleDef>? onBridging) {
			this.langSettings = langSettings ?? throw new ArgumentNullException(nameof(langSettings));
			editTracker = ModuleEditTracker.Shared;
			cache = new ModuleBridgeCache(new ModuleBridgeOptions(), module => {
				bool modified = editTracker.IsModified(module);
				onBridging?.Invoke(module);
				return modified;
			});
			DocumentationProvider = new DnlibDocumentationProvider(cache);
			typeSystems = new TypeSystemCache(cache);
		}

		public const string LanguageName = "C#";

		/// <summary>
		/// "ILSpy 11.2.0.10050", the version of the upstream decompiler (ICSharpCode.Decompiler)
		/// </summary>
		public static string EngineName {
			get {
				var version = typeof(UpstreamCSharpDecompilerEngine).Assembly.GetName().Version;
				return version is null ? "ILSpy" : "ILSpy " + version.ToString();
			}
		}

		public override DecompilerSettingsBase Settings => langSettings;
		public override string ContentTypeString => ContentTypeName;
		public override string GenericNameUI => DecompilerConstants.GENERIC_NAMEUI_CSHARP;
		public override string UniqueNameUI => LanguageName;
		public override double OrderUI => DecompilerConstants.CSHARP_ILSPY_ORDERUI;
		// The C# compiler (Edit Method etc.) is used for this generic language
		public override Guid GenericGuid => DecompilerConstants.LANGUAGE_CSHARP;
		public override Guid UniqueGuid => LanguageGuid;
		public override string FileExtension => ".cs";

		#region Edits
		/// <summary>
		/// Call it when modules have been edited (or saved): they're decompiled from an image written by dnlib from now on
		/// </summary>
		public void OnModulesModified(IEnumerable<ModuleDef> modules) {
			var list = modules.ToList();
			foreach (var module in list) {
				editTracker.MarkModified(module);
				cache.Invalidate(module);
			}
			UpstreamILAst.OnModulesModified(list);
		}

		/// <summary>
		/// Call it when assemblies have been added: references that couldn't be resolved may now resolve
		/// </summary>
		public void OnModulesAdded() {
			cache.OnAssembliesAdded();
			UpstreamILAst.OnModulesAdded();
		}

		/// <summary>
		/// Call it when assemblies have been removed (frees their images)
		/// </summary>
		public void OnModulesRemoved(IEnumerable<ModuleDef> modules) {
			var list = modules.ToList();
			foreach (var module in list)
				cache.Invalidate(module);
			UpstreamILAst.OnModulesRemoved(list);
		}

		/// <summary>
		/// Call it when all assemblies were removed or reloaded (frees all images)
		/// </summary>
		public void OnAllModulesRemoved() {
			cache.InvalidateAll();
			UpstreamILAst.OnAllModulesRemoved();
		}

		/// <summary>
		/// Method bodies edited with the IL editor or the C# compiler are marked in the context (it's set before the
		/// modified document event), so check them too
		/// </summary>
		internal void CheckModified(ModuleDef module, IEnumerable<MethodDef> methods, DecompilationContext ctx) {
			if (editTracker.IsModified(module))
				return;
			foreach (var method in methods) {
				if (ctx.IsBodyModified(method)) {
					OnModulesModified(new[] { module });
					return;
				}
			}
		}

		static IEnumerable<MethodDef> GetMethods(TypeDef type) {
			foreach (var t in type.GetTypes().Prepend(type)) {
				foreach (var m in t.Methods)
					yield return m;
			}
		}

		static IEnumerable<MethodDef> GetMethods(IMemberDef member) {
			switch (member) {
			case MethodDef method:
				return method.DeclaringType is TypeDef declType && method.IsConstructor ? declType.Methods : new[] { method };
			case PropertyDef property:
				return property.GetMethods.Concat(property.SetMethods).Concat(property.OtherMethods);
			case EventDef ev:
				return new[] { ev.AddMethod, ev.RemoveMethod, ev.InvokeMethod }.Where(a => a is not null)!;
			case FieldDef field when field.DeclaringType is TypeDef fieldType:
				return fieldType.Methods.Where(a => a.IsConstructor);
			default:
				return Array.Empty<MethodDef>();
			}
		}
		#endregion

		// In its namespace (like ILSpy and dnSpy's previous C# decompiler): Export to Project saves this code to a file
		public override void Decompile(TypeDef type, IDecompilerOutput output, DecompilationContext ctx) =>
			Decompile(type.Module, GetMethods(type), output, ctx, DecompileTypeInNamespace, type);

		SyntaxTree DecompileTypeInNamespace(UpstreamCSharpDecompilerEngine decompiler, EntityHandle handle) {
			// Upstream doesn't decompile hidden types (compiler-generated types, which the tree shows if the user wants
			// them) and an empty <Module> this way
			var typeHandle = (TypeDefinitionHandle)handle;
			var module = decompiler.TypeSystem.MainModule;
			var type = module.GetDefinition(typeHandle);
			if ((type.Name == "<Module>" && type.Members.Count == 0) || UpstreamCSharpDecompilerEngine.MemberIsHidden(module.MetadataFile, handle, langSettings.Settings))
				return decompiler.Decompile(handle);
			return decompiler.DecompileTypes(new[] { typeHandle });
		}

		public override void Decompile(MethodDef method, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, method);
			Decompile(method.Module, GetMethods(method), output, ctx, (decompiler, handle) => {
				var methodDefinition = decompiler.TypeSystem.MainModule.GetDefinition((MethodDefinitionHandle)handle);
				if (methodDefinition.IsConstructor && methodDefinition.DeclaringType.IsReferenceType != false && methodDefinition.DeclaringTypeDefinition is ITypeDefinition ctorDeclType) {
					// Also decompile the fields so their initializers are shown
					var members = CollectFieldsAndCtors(ctorDeclType, methodDefinition.IsStatic);
					decompiler.AstTransforms.Add(new SelectCtorTransform(methodDefinition));
					return decompiler.Decompile(members);
				}
				return decompiler.Decompile(handle);
			}, method);
		}

		public override void Decompile(PropertyDef property, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, property);
			Decompile(property.Module, GetMethods(property), output, ctx, (decompiler, handle) => decompiler.Decompile(handle), property);
		}

		public override void Decompile(EventDef ev, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, ev);
			Decompile(ev.Module, GetMethods(ev), output, ctx, (decompiler, handle) => decompiler.Decompile(handle), ev);
		}

		public override void Decompile(FieldDef field, IDecompilerOutput output, DecompilationContext ctx) {
			WriteCommentLineDeclaringType(output, field);
			Decompile(field.Module, GetMethods(field), output, ctx, (decompiler, handle) => {
				var fieldDefinition = decompiler.TypeSystem.MainModule.GetDefinition((FieldDefinitionHandle)handle);
				if (fieldDefinition.IsConst || !(fieldDefinition.DeclaringTypeDefinition is ITypeDefinition fieldDeclType))
					return decompiler.Decompile(handle);
				// Also decompile the constructors so the field initializer is shown
				var members = CollectFieldsAndCtors(fieldDeclType, fieldDefinition.IsStatic);
				decompiler.AstTransforms.Add(new SelectFieldTransform(fieldDefinition));
				return decompiler.Decompile(members);
			}, field);
		}

		public override void Decompile(AssemblyDef asm, IDecompilerOutput output, DecompilationContext ctx) {
			WriteAssembly(asm, output, ctx);
			if (asm.ManifestModule is null)
				return;
			// dnSpy resolves references to temporary modules while it doesn't load assemblies: don't cache anything built from them
			using (ctx.DisableAssemblyLoad())
				Decompile(asm.ManifestModule, Array.Empty<MethodDef>(), output, ctx, (decompiler, handle) => decompiler.DecompileModuleAndAssemblyAttributes(), null, cacheTypeSystem: false);
		}

		public override void Decompile(ModuleDef mod, IDecompilerOutput output, DecompilationContext ctx) {
			WriteModule(mod, output, ctx);
			using (ctx.DisableAssemblyLoad())
				Decompile(mod, Array.Empty<MethodDef>(), output, ctx, (decompiler, handle) => decompiler.DecompileModuleAndAssemblyAttributes(), null, cacheTypeSystem: false);
		}

		/// <param name="configure">Changes the settings or the decompiler for this decompilation</param>
		/// <param name="createImage">Creates another image of the module to decompile (same tokens), or returns null to
		/// use the module's image</param>
		/// <param name="writeHeader">Writes text before the code</param>
		void Decompile(ModuleDef module, IEnumerable<MethodDef> methods, IDecompilerOutput output, DecompilationContext ctx,
			Func<UpstreamCSharpDecompilerEngine, EntityHandle, SyntaxTree> decompile, IMemberDef? definition, bool cacheTypeSystem = true,
			Action<DecompilerSettings>? configureSettings = null, Action<UpstreamCSharpDecompilerEngine, BridgedModule>? configure = null,
			Func<BridgedModule, BridgedModule?>? createImage = null, Action<IDecompilerOutput>? writeHeader = null) {
			CheckModified(module, methods, ctx);
			// The version first: if the settings change now, the debug info gets the old version and is recreated
			int settingsVersion = langSettings.Version;
			var settings = langSettings.CreateDecompilationSettings();
			configureSettings?.Invoke(settings);
			bool showTokens = langSettings.ShowTokenAndRvaComments;
			BridgedModule bridged;
			try {
				bridged = cache.Get(module);
			}
			catch (ModuleBridgeException ex) {
				WriteError(output, "The module can't be decompiled: " + ex.Message);
				return;
			}
			var handle = default(EntityHandle);
			if (definition is not null) {
				int token = bridged.Tokens.GetToken(definition);
				if (token == 0) {
					WriteError(output, "The member isn't part of the image that was given to the decompiler.");
					return;
				}
				handle = MetadataTokens.EntityHandle(token);
			}

			var image = createImage?.Invoke(bridged);
			if (image is not null)
				bridged = image;

			BigStackRunner.Run(() => {
				var typeSystem = image is not null ? typeSystems.CreateForImage(image, settings) : typeSystems.Get(bridged, settings, cacheTypeSystem);
				var decompiler = new UpstreamCSharpDecompilerEngine(typeSystem, settings) {
					CancellationToken = ctx.CancellationToken,
					DocumentationProvider = DocumentationProvider,
				};
				if (settings.UseDebugSymbols)
					decompiler.DebugInfoProvider = DnlibDebugInfoProvider.TryCreate(bridged);
				// (Before the using declarations are added: it references a type)
				int initializers = decompiler.AstTransforms.IndexOf(decompiler.AstTransforms.OfType<TransformFieldAndConstructorInitializers>().FirstOrDefault()!);
				if (initializers >= 0) {
					decompiler.AstTransforms.Insert(initializers + 1, new Transforms.ConstructorParameterInitializerTransform());
					// (After DeclareVariables. Before the field initializers: the code that fills a collection in a
					// constructor becomes one assignment, which can be an initializer.)
					decompiler.AstTransforms.Insert(initializers, new Transforms.CollectionExpressionTransform());
				}
				int usings = decompiler.AstTransforms.IndexOf(decompiler.AstTransforms.OfType<IntroduceUsingDeclarations>().FirstOrDefault()!);
				decompiler.AstTransforms.Insert(usings < 0 ? decompiler.AstTransforms.Count : usings, new Transforms.CompilerHelperCallTransform());
				decompiler.AstTransforms.Insert(usings < 0 ? decompiler.AstTransforms.Count : usings + 1, new Transforms.CompoundAssignmentOperatorTransform());
				decompiler.AstTransforms.Insert(usings < 0 ? decompiler.AstTransforms.Count : usings + 2, new Transforms.CompoundAssignmentOperatorDeclarationTransform());
				if (showTokens)
					decompiler.AstTransforms.Add(new TokenCommentTransform(cache));
				configure?.Invoke(decompiler, bridged);
				var syntaxTree = decompile(decompiler, handle);
				writeHeader?.Invoke(output);
				WriteCode(syntaxTree, decompiler, settings, settingsVersion, module, output);
			});
		}

		void WriteCode(SyntaxTree syntaxTree, UpstreamCSharpDecompilerEngine decompiler, DecompilerSettings settings, int settingsVersion, ModuleDef module, IDecompilerOutput output) {
			syntaxTree.AcceptVisitor(new InsertParenthesesVisitor { InsertParenthesesForReadability = true });
			var references = new ReferenceFactory(cache, module);
			var variables = new SourceVariableTable(cache, references.TypeSigConverter);
			var textOutput = new DnSpyTextOutput(output, references, variables);
			var tokenWriter = TokenWriter.InsertRequiredSpaces(TokenWriter.WrapInWriterThatSetsLocationsInAST(new DnSpyTokenWriter(textOutput, settings, decompiler.TypeSystem)));
			syntaxTree.AcceptVisitor(new DnSpyOutputVisitor(tokenWriter, settings.CSharpFormattingOptions));

			// Debug info is custom data (the debugger and the text view use it, dnSpy.Console doesn't)
			if (output.UsesCustomData) {
				var sequencePoints = decompiler.CreateSequencePoints(syntaxTree);
				var repairs = new List<string>();
				foreach (var info in DebugInfoFactory.Create(syntaxTree, sequencePoints, textOutput, cache, variables, settingsVersion, repairs))
					output.AddDebugInfo(info);
			}
		}

		void WriteError(IDecompilerOutput output, string message) {
			foreach (var line in message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)) {
				WriteCommentBegin(output, true);
				output.Write(line, BoxedTextColor.Comment);
				WriteCommentEnd(output, true);
				output.WriteLine();
			}
		}

		#region Single member decompilation (same as ILSpy)
		static List<EntityHandle> CollectFieldsAndCtors(ITypeDefinition type, bool isStatic) {
			var members = new List<EntityHandle>();
			foreach (var field in type.Fields) {
				if (!field.MetadataToken.IsNil && field.IsStatic == isStatic)
					members.Add(field.MetadataToken);
			}
			foreach (var e in type.Events) {
				if (!e.MetadataToken.IsNil && e.IsStatic == isStatic)
					members.Add(e.MetadataToken);
			}
			foreach (var p in type.Properties) {
				if (!p.MetadataToken.IsNil && p.IsStatic == isStatic && !p.IsIndexer)
					members.Add(p.MetadataToken);
			}
			foreach (var ctor in type.Methods) {
				if (!ctor.MetadataToken.IsNil && ctor.IsConstructor && ctor.IsStatic == isStatic)
					members.Add(ctor.MetadataToken);
			}
			return members;
		}

		/// <summary>
		/// Removes all members except the constructor and the members with initializers
		/// </summary>
		sealed class SelectCtorTransform : IAstTransform {
			readonly UpstreamMethod ctor;
			readonly HashSet<ISymbol?> removedSymbols = new HashSet<ISymbol?>();

			public SelectCtorTransform(UpstreamMethod ctor) => this.ctor = ctor;

			public void Run(AstNode rootNode, TransformContext context) {
				ConstructorDeclaration? ctorDecl = null;
				foreach (var node in rootNode.Children.ToArray()) {
					switch (node) {
					case ConstructorDeclaration cd:
						if (cd.GetSymbol() == ctor)
							ctorDecl = cd;
						else
							Remove(node);
						break;
					case FieldDeclaration fd:
						if (fd.Variables.All(v => v.Initializer is null))
							Remove(node);
						break;
					case EventDeclaration ed:
						if (ed.Variables.All(v => v.Initializer is null))
							Remove(node);
						break;
					case PropertyDeclaration pd:
						if (pd.Initializer is null)
							Remove(node);
						break;
					case CustomEventDeclaration:
					case IndexerDeclaration:
						Remove(node);
						break;
					}
				}
				if (ctorDecl?.Initializer?.ConstructorInitializerType == ConstructorInitializerType.This) {
					// The other constructor initializes the fields. (Unlike ILSpy, keep the using declarations: they're
					// the debug info's imports.)
					foreach (var node in rootNode.Children.ToArray()) {
						if (node is EntityDeclaration && !(node is ConstructorDeclaration))
							Remove(node);
					}
				}
				foreach (var node in rootNode.Children.ToArray()) {
					if (node is Comment && removedSymbols.Contains(node.GetSymbol()))
						node.Remove();
				}
			}

			void Remove(AstNode node) {
				node.Remove();
				removedSymbols.Add(node.GetSymbol());
			}
		}

		/// <summary>
		/// Removes all members except the field
		/// </summary>
		sealed class SelectFieldTransform : IAstTransform {
			readonly UpstreamField field;

			public SelectFieldTransform(UpstreamField field) => this.field = field;

			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var node in rootNode.Children.ToArray()) {
					switch (node) {
					case EntityDeclaration:
					case Comment:
						if (node.GetSymbol() != field)
							node.Remove();
						break;
					}
				}
			}
		}
		#endregion

		#region Names and visibility
		public override bool ShowMember(IMemberRef member) {
			if (langSettings.ShowHiddenMembers)
				return true;
			// The tree view shows accessors below their property/event
			if (member is MethodDef md && (md.IsGetter || md.IsSetter || md.IsAddOn || md.IsRemoveOn))
				return true;
			// The compiler's types of collection expressions for interfaces (CollectionExpressionTransform)
			if (member is TypeDef type && Transforms.CollectionExpressionTransform.IsCompilerType(type))
				return false;
			if (member is IMemberDef def)
				return !memberVisibility.IsHidden(def, langSettings.Settings);
			return true;
		}

		const FormatterOptions TypeNameOptions = FormatterOptions.ShowIntrinsicTypeKeywords | FormatterOptions.ShowDeclaringTypes;

		protected override void TypeToString(IDecompilerOutput output, ITypeDefOrRef? type, bool includeNamespace, IHasCustomAttribute? typeAttributes = null) {
			if (type is null)
				return;
			if (type is TypeSpec) {
				var options = includeNamespace ? TypeNameOptions | FormatterOptions.ShowNamespaces : TypeNameOptions;
				new dnSpy.Decompiler.CSharp.CSharpFormatter(new DecompilerOutputTextColorWriter(output), options, null).Write(type);
				return;
			}
			WriteTypeName(output, type, includeNamespace, includeDeclaringTypes: true, useKeywords: true, depth: 0);
		}

		protected override void FormatTypeName(IDecompilerOutput output, TypeDef type) {
			if (type is null)
				throw new ArgumentNullException(nameof(type));
			WriteTypeName(output, type, includeNamespace: false, includeDeclaringTypes: false, useKeywords: false, depth: 0);
		}

		protected override void FormatPropertyName(IDecompilerOutput output, PropertyDef property, bool? isIndexer) {
			if (property is null)
				throw new ArgumentNullException(nameof(property));
			if (!(isIndexer ?? property.IsIndexer())) {
				output.Write(IdentifierEscaper.Escape(property.Name), MetadataTextColorProvider.GetColor(property));
				return;
			}
			var accessor = property.GetMethod ?? property.SetMethod;
			if (accessor is not null && accessor.HasOverrides && accessor.Overrides[0].MethodDeclaration?.DeclaringType is ITypeDefOrRef explicitInterface) {
				TypeToString(output, explicitInterface, includeNamespace: true);
				output.Write(".", BoxedTextColor.Operator);
			}
			output.Write("this", BoxedTextColor.Keyword);
			output.Write("[", BoxedTextColor.Punctuation);
			bool first = true;
			foreach (var p in property.PropertySig?.GetParams() ?? (IList<TypeSig>)Array.Empty<TypeSig>()) {
				if (!first) {
					output.Write(",", BoxedTextColor.Punctuation);
					output.Write(" ", BoxedTextColor.Text);
				}
				first = false;
				TypeToString(output, p.ToTypeDefOrRef(), includeNamespace: true);
			}
			output.Write("]", BoxedTextColor.Punctuation);
		}

		/// <summary>
		/// Writes "Namespace.Outer.Name&lt;T&gt;" (only the type's own generic parameters, like C#)
		/// </summary>
		void WriteTypeName(IDecompilerOutput output, ITypeDefOrRef type, bool includeNamespace, bool includeDeclaringTypes, bool useKeywords, int depth) {
			if (depth > 50)
				return;
			if (useKeywords && GetKeyword(type) is string keyword) {
				output.Write(keyword, BoxedTextColor.Keyword);
				return;
			}
			var declaringType = type.DeclaringType;
			if (includeDeclaringTypes && declaringType is not null) {
				WriteTypeName(output, declaringType, includeNamespace, includeDeclaringTypes, useKeywords: false, depth + 1);
				output.Write(".", BoxedTextColor.Operator);
			}
			else if (includeNamespace && !string.IsNullOrEmpty(type.Namespace)) {
				foreach (var part in type.Namespace.Split('.')) {
					output.Write(IdentifierEscaper.Escape(part), BoxedTextColor.Namespace);
					output.Write(".", BoxedTextColor.Operator);
				}
			}
			var name = type.Name;
			int tick = name.LastIndexOf('`');
			int ownGenericParams = -1;
			if (tick >= 0 && int.TryParse(name.Substring(tick + 1), out int count)) {
				ownGenericParams = count;
				name = name.Substring(0, tick);
			}
			output.Write(IdentifierEscaper.Escape(name), MetadataTextColorProvider.GetColor(type));
			if (type is TypeDef td && td.GenericParameters.Count > 0) {
				int total = td.GenericParameters.Count;
				if (ownGenericParams < 0)
					ownGenericParams = declaringType is TypeDef declTd ? Math.Max(0, total - declTd.GenericParameters.Count) : total;
				ownGenericParams = Math.Min(ownGenericParams, total);
				if (ownGenericParams > 0) {
					output.Write("<", BoxedTextColor.Punctuation);
					for (int i = total - ownGenericParams; i < total; i++) {
						if (i > total - ownGenericParams) {
							output.Write(",", BoxedTextColor.Punctuation);
							output.Write(" ", BoxedTextColor.Text);
						}
						var gp = td.GenericParameters[i];
						output.Write(IdentifierEscaper.Escape(gp.Name), MetadataTextColorProvider.GetColor(gp));
					}
					output.Write(">", BoxedTextColor.Punctuation);
				}
			}
		}

		static string? GetKeyword(ITypeDefOrRef type) {
			if (type.DeclaringType is not null || type.Namespace != "System" || !type.DefinitionAssembly.IsCorLib())
				return null;
			switch (type.TypeName) {
			case "Void": return "void";
			case "Boolean": return "bool";
			case "Byte": return "byte";
			case "Char": return "char";
			case "Decimal": return "decimal";
			case "Double": return "double";
			case "Int16": return "short";
			case "Int32": return "int";
			case "Int64": return "long";
			case "Object": return "object";
			case "SByte": return "sbyte";
			case "Single": return "float";
			case "String": return "string";
			case "UInt16": return "ushort";
			case "UInt32": return "uint";
			case "UInt64": return "ulong";
			default: return null;
			}
		}
		#endregion
	}

	/// <summary>
	/// Writes <see cref="ITextColorWriter"/> output to an <see cref="IDecompilerOutput"/>
	/// </summary>
	sealed class DecompilerOutputTextColorWriter : ITextColorWriter {
		readonly IDecompilerOutput output;
		public DecompilerOutputTextColorWriter(IDecompilerOutput output) => this.output = output;
		public void Write(object color, string? text) {
			if (!string.IsNullOrEmpty(text))
				output.Write(text!, color);
		}
		public void Write(TextColor color, string? text) => Write(color.Box(), text);
	}
}
