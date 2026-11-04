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
using dnSpy.Decompiler.ILSpy.Core;
using dnSpy.Decompiler.ILSpy.Core.Settings;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using ICSharpCode.Decompiler.Documentation;
using ICSharpCode.Decompiler.TypeSystem;
using Xunit;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// dnSpy changes the assembly list and edits modules while the decompiler runs (on other threads): the cached
	/// images and type systems must stay usable and up to date
	/// </summary>
	public class LanguageCacheTests {
		const string Program = """
			using System;
			using System.Collections.Generic;
			namespace N {
				public class C {
					public static int M(List<int> list) {
						Console.WriteLine(list.Count);
						try {
							return list[0];
						}
						catch (Exception ex) {
							return ex.HResult;
						}
					}
				}
			}
			""";

		static void AssertReferencesAndDebugInfo(CapturingDecompilerOutput output) {
			foreach (var name in new[] { "List", "Console", "Exception", "M" })
				Assert.Contains(output.Spans, s => s.Text == name && s.Reference is IMemberRef);
			Assert.Contains("// Token:", output.Text);
			Assert.Contains(output.DebugInfos, i => i.Method.Name == "M" && i.Statements.Length != 0);
		}

		[Fact]
		public void AssembliesAddedWhileModulesAreBridged_KeepReferencesAndDebugInfo() {
			var module = TestCompiler.Compile("P1", Program).Load();
			UpstreamCSharpLanguage? language = null;
			// The first decompilation auto-loads the references and dnSpy's UI thread handles the Add events while
			// the decompiler thread is still bridging (the module and its references)
			language = new UpstreamCSharpLanguage(new UpstreamDecompilerSettings(), m => language!.OnModulesAdded());
			var output = LanguageRunner.Decompile(language, module.Find("N.C", false));
			AssertReferencesAndDebugInfo(output);
			AssertReferencesAndDebugInfo(LanguageRunner.Decompile(language, module.Find("N.C", false)));
		}

		sealed class HookDocumentationProvider : IDocumentationProvider {
			public Action? OnFirstCall;
			public string? GetDocumentation(IEntity entity) {
				var action = OnFirstCall;
				OnFirstCall = null;
				action?.Invoke();
				return null;
			}
		}

		[Fact]
		public void AssembliesRemovedDuringDecompilation_KeepReferencesAndDebugInfo() {
			var module = TestCompiler.Compile("P1", Program).Load();
			var language = LanguageRunner.Create();
			language.DocumentationProvider = new HookDocumentationProvider { OnFirstCall = () => language.OnAllModulesRemoved() };
			AssertReferencesAndDebugInfo(LanguageRunner.Decompile(language, module.Find("N.C", false)));
			AssertReferencesAndDebugInfo(LanguageRunner.Decompile(language, module.Find("N.C", false)));
		}

		/// <summary>
		/// Like dnSpy's resolver: assemblies are added to the assembly explorer, except while loading is disabled
		/// (then it returns temporary modules)
		/// </summary>
		sealed class DocumentListResolver : dnlib.DotNet.IAssemblyResolver {
			readonly dnlib.DotNet.IAssemblyResolver inner;
			public bool LoadDisabled;
			public readonly Dictionary<string, AssemblyDef> Documents = new Dictionary<string, AssemblyDef>(StringComparer.OrdinalIgnoreCase);
			public readonly HashSet<ModuleDef> TemporaryModules = new HashSet<ModuleDef>();

			public DocumentListResolver(dnlib.DotNet.IAssemblyResolver inner) => this.inner = inner;

			public AssemblyDef? Resolve(IAssembly assembly, ModuleDef sourceModule) {
				if (Documents.TryGetValue(assembly.Name, out var document))
					return document;
				var asm = inner.Resolve(assembly, sourceModule);
				if (asm is null)
					return null;
				if (LoadDisabled) {
					var temporary = ModuleDefMD.Load(asm.ManifestModule.Location, sourceModule.Context).Assembly;
					TemporaryModules.Add(temporary.ManifestModule);
					return temporary;
				}
				Documents[assembly.Name] = asm;
				return asm;
			}
		}

		sealed class Disposable : IDisposable {
			readonly Action action;
			public Disposable(Action action) => this.action = action;
			public void Dispose() => action();
		}

		[Fact]
		public void AssemblyDecompiledWithoutAssemblyLoading_DoesNotCacheTemporaryModules() {
			var compiled = TestCompiler.Compile("P1", Program);
			var context = TestCompiler.CreateModuleContext();
			var resolver = new DocumentListResolver(context.AssemblyResolver);
			context.AssemblyResolver = resolver;
			context.Resolver = new Resolver(resolver);
			var module = ModuleDefMD.Load(compiled.Image, context);
			module.Location = "P1.dll";
			var language = LanguageRunner.Create();

			// dnSpy shows the assembly node first
			var ctx = LanguageRunner.CreateContext();
			ctx.GetDisableAssemblyLoad = () => {
				resolver.LoadDisabled = true;
				return new Disposable(() => resolver.LoadDisabled = false);
			};
			language.Decompile(module.Assembly, new CapturingDecompilerOutput(), ctx);
			Assert.NotEmpty(resolver.TemporaryModules);

			var output = LanguageRunner.Decompile(language, module.Find("N.C", false));
			AssertReferencesAndDebugInfo(output);
			foreach (var name in new[] { "List", "Console" }) {
				var reference = (IMemberRef)output.Spans.First(s => s.Text == name && s.Reference is IMemberRef).Reference!;
				Assert.DoesNotContain(reference.Module, resolver.TemporaryModules);
				Assert.Contains(reference.Module.Assembly, resolver.Documents.Values);
			}
		}

		[Fact]
		public void EditedReferencedModule_IsUsedByModulesThatReferenceIt() {
			var library = TestCompiler.Compile("Library", "public static class Library { public static int Ext(this int value) => value; }");
			var context = TestCompiler.CreateModuleContext();
			var libraryModule = ModuleDefMD.Load(library.Image, context);
			libraryModule.Location = "Library.dll";
			((AssemblyResolver)context.AssemblyResolver).AddToCache(libraryModule);
			var app = TestCompiler.Compile("App", "public static class App { public static int Run() => 5.Ext(); }", references: new[] { library });
			var appModule = ModuleDefMD.Load(app.Image, context);
			appModule.Location = "App.dll";
			var language = LanguageRunner.Create(s => s.ShowTokenAndRvaComments = false);
			var ext = libraryModule.Find("Library", false).FindMethod("Ext");
			var output = LanguageRunner.Decompile(language, appModule.Find("App", false));
			Assert.Contains("return 5.Ext();", output.Text);
			Assert.Same(ext, output.Spans.Single(s => s.Text == "Ext").Reference);

			// Edited in dnSpy: it's no longer an extension method
			var extensionAttribute = ext.CustomAttributes.Single(a => a.TypeFullName == "System.Runtime.CompilerServices.ExtensionAttribute");
			ext.CustomAttributes.Remove(extensionAttribute);
			language.OnModulesModified(new[] { libraryModule });
			output = LanguageRunner.Decompile(language, appModule.Find("App", false));
			Assert.Contains("return Library.Ext(5);", output.Text);
			Assert.Same(ext, output.Spans.Single(s => s.Text == "Ext").Reference);
		}

		[Fact]
		public void NamespaceReferences_UseTheAssemblyThatDefinesTheNamespace() {
			var module = TestCompiler.Compile("P1", Program + "namespace N2 { public class D { public static void M(N.C c) { } } }").Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("N2.D", false));
			var namespaces = output.Spans.Select(s => s.Reference).OfType<NamespaceReference>().ToArray();
			var own = namespaces.First(a => a.Namespace == "N");
			Assert.Equal("P1", own.Assembly!.Name.String);
			output = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("N.C", false));
			namespaces = output.Spans.Select(s => s.Reference).OfType<NamespaceReference>().ToArray();
			var generic = namespaces.First(a => a.Namespace == "System.Collections.Generic");
			Assert.NotNull(generic.Assembly);
			Assert.NotEqual("P1", generic.Assembly!.Name.String);
			Assert.Equal(typeof(List<>).Assembly.GetName().Name, generic.Assembly.Name.String);
		}

		[Fact]
		public void KeywordIdentifiers_DoNotShiftStatements() {
			var module = TestCompiler.Compile("P1", "public class D { public static int M(int @class) { if (@class > 0) return @class; return 0; } }").Load();
			var output = LanguageRunner.Decompile(LanguageRunner.Create(), module.Find("D", false).FindMethod("M"));
			var info = output.DebugInfos.Single(i => i.Method.Name == "M");
			var statements = info.Statements.Select(s => output.Text.Substring(s.TextSpan.Start, s.TextSpan.Length)).ToArray();
			Assert.Contains("return @class;", statements);
			Assert.Contains("if (@class > 0)", statements);
		}
	}
}
