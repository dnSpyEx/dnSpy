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
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Core.Bridge;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.CSharp.Transforms;
using ICSharpCode.Decompiler.TypeSystem;

namespace dnSpy.Decompiler.ILSpy.Core {
	// Export to Project (dnSpy's project writer, dnSpy.Decompiler.MSBuild, decompiles each type with this decompiler)
	sealed partial class UpstreamCSharpLanguage : IProjectPropertiesProvider {
		public override string? ProjectFileExtension => ".csproj";

		/// <summary>
		/// A part of a partial type: the code-behind of a XAML file (without the members the XAML compiler generates),
		/// the designer file of a WinForms form (only InitializeComponent, Dispose and the fields they use) and the
		/// form without them, a settings class
		/// </summary>
		void DecompilePartialType(DecompilePartialType info) {
			var type = info.Type;
			Decompile(type.Module, GetMethods(type), info.Output, info.Context, DecompileTypeInNamespace, type,
				configureSettings: settings => {
					// WinForms designer files use full type names
					if (!info.UseUsingDeclarations)
						settings.UsingDeclarations = false;
				},
				configure: (decompiler, bridged) => {
					// Before the using declarations are added so they're only the ones the code that's left uses
					int usings = decompiler.AstTransforms.IndexOf(decompiler.AstTransforms.OfType<IntroduceUsingDeclarations>().FirstOrDefault()!);
					var transform = new PartialTypeTransform(cache, info);
					if (usings < 0)
						decompiler.AstTransforms.Add(transform);
					else
						decompiler.AstTransforms.Insert(usings, transform);
				});
		}

		sealed class PartialTypeTransform : IAstTransform {
			readonly ModuleBridgeCache cache;
			readonly DecompilePartialType info;
			readonly HashSet<string> interfacesToRemove;

			public PartialTypeTransform(ModuleBridgeCache cache, DecompilePartialType info) {
				this.cache = cache;
				this.info = info;
				// Compared by name: the interface can be in another assembly than the project writer expects (e.g. .NET
				// moved IComponentConnector from WindowsBase to System.Xaml)
				interfacesToRemove = new HashSet<string>(info.InterfacesToRemove.Select(a => a.ReflectionFullName), StringComparer.Ordinal);
			}

			IMemberDef? ToDnlib(AstNode node) => node.GetSymbol() is IEntity entity ? cache.ToDnlib(entity) as IMemberDef : null;

			public void Run(AstNode rootNode, TransformContext context) {
				foreach (var entity in rootNode.Descendants.OfType<EntityDeclaration>().ToArray()) {
					if (entity.Parent is null)
						continue;
					var def = ToDnlib(entity);
					if (def is null)
						continue;
					if (def == info.Type) {
						if (entity is not TypeDeclaration typeDecl)
							continue;
						if (info.AddPartialKeyword && typeDecl.ClassType != ClassType.Enum)
							typeDecl.Modifiers |= Modifiers.Partial;
						foreach (var baseType in typeDecl.BaseTypes.ToArray()) {
							if (baseType.GetSymbol() is ICSharpCode.Decompiler.TypeSystem.IType type && interfacesToRemove.Contains(type.GetDefinition()?.ReflectionName ?? type.ReflectionName))
								baseType.Remove();
						}
						continue;
					}
					// Members of the type and its nested types
					if (!IsInType(def))
						continue;
					bool remove = info.ShowDefinitions ? !info.Definitions.Contains(def) : info.Definitions.Contains(def);
					if (remove)
						entity.Remove();
				}
			}

			bool IsInType(IMemberDef def) {
				for (var t = def.DeclaringType; t is not null; t = t.DeclaringType) {
					if (t == info.Type)
						return true;
				}
				return false;
			}
		}

		#region Project properties
		public IEnumerable<KeyValuePair<string, string>> GetProjectProperties(ModuleDef module) {
			// The C# version the code needs (the decompiler uses the newer language features that the settings allow)
			yield return new KeyValuePair<string, string>("LangVersion", ToLangVersion(langSettings.GetMinimumRequiredVersion()));
			// The code has nullable annotations ('string?'): annotations only, the compiler recreates the nullable metadata
			// without warnings about the code
			if (langSettings.Settings.NullableReferenceTypes && UsesNullableMetadata(module))
				yield return new KeyValuePair<string, string>("Nullable", "annotations");
			// Uses of [Experimental] APIs are errors unless they're suppressed, like the original project did
			var experimentalIds = GetExperimentalDiagnosticIds(module);
			if (experimentalIds.Count != 0)
				yield return new KeyValuePair<string, string>("NoWarn", "$(NoWarn);" + string.Join(";", experimentalIds));
		}

		const string ExperimentalAttribute = "System.Diagnostics.CodeAnalysis.ExperimentalAttribute";

		/// <summary>
		/// The diagnostic IDs of the [Experimental] APIs the module defines or uses
		/// </summary>
		static List<string> GetExperimentalDiagnosticIds(ModuleDef module) {
			var ids = new SortedSet<string>(StringComparer.Ordinal);
			var checkedOwners = new HashSet<object>();
			void Add(IHasCustomAttribute? owner) {
				if (owner is null || !checkedOwners.Add(owner))
					return;
				foreach (var ca in owner.CustomAttributes) {
					if (ca.TypeFullName == ExperimentalAttribute && ca.ConstructorArguments.Count == 1 && ca.ConstructorArguments[0].Value is UTF8String id && !UTF8String.IsNullOrEmpty(id))
						ids.Add(id.String);
				}
			}
			void AddMember(IMemberDef? def) {
				if (def is null)
					return;
				Add(def);
				for (var t = def.DeclaringType; t is not null; t = t.DeclaringType)
					Add(t);
				Add(def.Module);
				Add(def.Module?.Assembly);
			}

			Add(module);
			Add(module.Assembly);
			foreach (var type in module.GetTypes()) {
				Add(type);
				foreach (var m in type.Methods)
					Add(m);
				foreach (var f in type.Fields)
					Add(f);
				foreach (var p in type.Properties)
					Add(p);
				foreach (var e in type.Events)
					Add(e);
			}
			// The referenced APIs (and their types and assemblies)
			try {
				foreach (var typeRef in module.GetTypeRefs())
					AddMember(typeRef.Resolve());
				if (module is ModuleDefMD md) {
					uint rows = md.TablesStream.MemberRefTable.Rows;
					for (uint rid = 1; rid <= rows; rid++) {
						switch (md.ResolveMemberRef(rid)?.Resolve()) {
						case MethodDef method:
							AddMember(method);
							foreach (var p in method.DeclaringType?.Properties.Where(a => a.GetMethods.Contains(method) || a.SetMethods.Contains(method)) ?? Enumerable.Empty<PropertyDef>())
								Add(p);
							break;
						case FieldDef field:
							AddMember(field);
							break;
						}
					}
				}
			}
			catch (Exception ex) when (ex is ResolveException || ex is BadImageFormatException || ex is System.IO.IOException) {
			}
			return ids.ToList();
		}

		/// <summary>
		/// LangVersion of a C# version: "7.3", "14.0", "preview"
		/// </summary>
		internal static string ToLangVersion(LanguageVersion version) {
			switch (version) {
			case LanguageVersion.Latest:
				return "latest";
			case LanguageVersion.CSharp1:
				return "ISO-1";
			case LanguageVersion.CSharp2:
				return "ISO-2";
			}
			// Preview is the next version (upstream's newest features may need a newer compiler than the latest release)
			if (version >= LanguageVersion.Preview)
				return "preview";
			int value = (int)version;
			if (value < 100)
				return value.ToString();
			return $"{value / 100}.{value % 100}";
		}

		static readonly string[] nullableAttributes = {
			"System.Runtime.CompilerServices.NullableAttribute",
			"System.Runtime.CompilerServices.NullableContextAttribute",
		};

		static bool UsesNullableMetadata(ModuleDef module) {
			foreach (var name in nullableAttributes) {
				if (module.Find(name, isReflectionName: false) is not null)
					return true;
			}
			foreach (var typeRef in module.GetTypeRefs()) {
				if (Array.IndexOf(nullableAttributes, typeRef.FullName) >= 0)
					return true;
			}
			return false;
		}
		#endregion
	}
}
