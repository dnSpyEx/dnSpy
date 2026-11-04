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
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using dnlib.DotNet;
using dnSpy.AsmEditor.Compiler;
using dnSpy.Contracts.AsmEditor.Compiler;
using dnSpy.Contracts.Decompiler;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;

namespace dnSpy.Decompiler.ILSpy.Tests.Infrastructure {
	/// <summary>The edit code commands (Edit Method, Edit Class, Add Class Members, Edit Assembly Attributes)</summary>
	enum EditKind {
		EditMethod,
		EditClass,
		AddMembers,
		EditAssembly,
	}

	readonly record struct CaretSpan(int Start, int Length);

	sealed record CompileError(string Id, string Message, string Document, string Code) {
		public static CompileError Create(Diagnostic d) {
			var tree = d.Location.SourceTree;
			return new CompileError(d.Id, d.GetMessage(), tree?.FilePath ?? "", tree is null ? "" : tree.GetText().ToString(d.Location.SourceSpan));
		}
	}

	sealed class EditDocument {
		public string Name { get; }
		public string Text { get; set; }
		/// <summary>Where the edit dialog puts the caret (the edited member's name or statement)</summary>
		public CaretSpan? Caret { get; }

		public EditDocument(string name, string text, CaretSpan? caret) {
			Name = name;
			Text = text;
			Caret = caret;
		}
	}

	sealed class EditResult {
		public EditKind Kind { get; }
		public string Target { get; }
		public List<EditDocument> Documents { get; } = new List<EditDocument>();
		public string? DecompileError { get; set; }
		/// <summary>
		/// The compiler's errors (formatted when compiling: the references' metadata is freed after the import, like
		/// in dnSpy)
		/// </summary>
		public List<CompileError> CompileErrors { get; } = new List<CompileError>();
		public List<string> ImportErrors { get; } = new List<string>();
		public bool Compiled => DecompileError is null && CompileErrors.Count == 0;
		public bool Imported => Compiled && ImportErrors.Count == 0;

		public EditResult(EditKind kind, string target) {
			Kind = kind;
			Target = target;
		}

		public string Describe(int maxErrors = 5) {
			if (DecompileError is not null)
				return $"{Kind} {Target}: decompilation failed: {DecompileError}";
			if (CompileErrors.Count != 0)
				return $"{Kind} {Target}: {CompileErrors.Count} compiler errors: " + string.Join(" | ", CompileErrors.Take(maxErrors).Select(Format));
			if (ImportErrors.Count != 0)
				return $"{Kind} {Target}: import errors: " + string.Join(" | ", ImportErrors.Take(maxErrors));
			return $"{Kind} {Target}: OK";
		}

		static string Format(CompileError e) => $"{e.Id} {e.Message} [{e.Document}: `{(e.Code.Length > 80 ? e.Code.Substring(0, 80) : e.Code)}`]";
	}

	/// <summary>
	/// dnSpy's edit code dialog without the UI: decompiles the documents like the edit view models
	/// (EditMethodCodeVM, EditClassVM, AddMembersCodeVM, EditAssemblyVM), compiles them like the C# language compiler
	/// (dnSpy.Roslyn's RoslynLanguageCompiler: the same references, patched by dnSpy's AssemblyReferenceResolver, and
	/// options), imports the result with dnSpy's ModuleImporter and applies it to the module like the commands do.
	/// </summary>
	static class EditPipeline {
		const string MainCodeName = "main.cs";
		const string MainGeneratedCodeName = "main.g.cs";

		public static EditResult Decompile(IDecompiler decompiler, EditKind kind, ModuleDef module, IMemberDef? member) {
			var target = member?.FullName ?? module.Name;
			var result = new EditResult(kind, target);
			try {
				switch (kind) {
				case EditKind.EditMethod: {
					var method = (MethodDef)member!;
					var type = GetNonNestedType(method.DeclaringType);
					var ctx = new DecompilationContext { CalculateILSpans = true, AsyncMethodBodyDecompilation = false };
					var main = new ReferenceDecompilerOutput(method);
					var options = new DecompileTypeMethods(main, ctx, type);
					options.Methods.Add(method);
					options.DecompileHidden = false;
					decompiler.Decompile(DecompilationType.TypeMethods, options);
					ctx.CalculateILSpans = false;
					var hidden = new StringBuilderDecompilerOutput();
					options = new DecompileTypeMethods(hidden, ctx, type);
					options.Methods.Add(method);
					options.DecompileHidden = true;
					decompiler.Decompile(DecompilationType.TypeMethods, options);
					result.Documents.Add(new EditDocument(MainCodeName, main.ToString(), main.Span));
					result.Documents.Add(new EditDocument(MainGeneratedCodeName, hidden.ToString(), null));
					break;
				}
				case EditKind.EditClass: {
					var type = GetNonNestedType(member as TypeDef ?? member!.DeclaringType);
					var ctx = new DecompilationContext { CalculateILSpans = true, AsyncMethodBodyDecompilation = false };
					var main = new ReferenceDecompilerOutput(member!);
					var options = new DecompileTypeMethods(main, ctx, type) { DecompileHidden = false, ShowAll = true };
					decompiler.Decompile(DecompilationType.TypeMethods, options);
					result.Documents.Add(new EditDocument(MainCodeName, main.ToString(), main.Span));
					break;
				}
				case EditKind.AddMembers: {
					var type = GetNonNestedType(member as TypeDef ?? member!.DeclaringType);
					var ctx = new DecompilationContext { AsyncMethodBodyDecompilation = false };
					var main = new ReferenceDecompilerOutput(member as TypeDef ?? member!.DeclaringType);
					var options = new DecompileTypeMethods(main, ctx, type) { DecompileHidden = false };
					options.Types.Add(type);
					foreach (var t in type.GetTypes())
						options.Types.Add(t);
					decompiler.Decompile(DecompilationType.TypeMethods, options);
					var hidden = new StringBuilderDecompilerOutput();
					options = new DecompileTypeMethods(hidden, ctx, type) { DecompileHidden = true };
					options.Types.Add(type);
					foreach (var t in type.GetTypes())
						options.Types.Add(t);
					decompiler.Decompile(DecompilationType.TypeMethods, options);
					result.Documents.Add(new EditDocument(MainCodeName, main.ToString(), main.Span));
					result.Documents.Add(new EditDocument(MainGeneratedCodeName, hidden.ToString(), null));
					break;
				}
				case EditKind.EditAssembly: {
					var ctx = new DecompilationContext { AsyncMethodBodyDecompilation = false };
					var main = new StringBuilderDecompilerOutput();
					var options = new DecompileAssemblyInfo(main, ctx, module) { KeepAllAttributes = true };
					decompiler.Decompile(DecompilationType.AssemblyInfo, options);
					result.Documents.Add(new EditDocument(MainCodeName, main.ToString(), null));
					break;
				}
				}
			}
			catch (Exception ex) {
				result.DecompileError = ex.ToString();
			}
			return result;
		}

		static TypeDef GetNonNestedType(TypeDef type) {
			while (type.DeclaringType is not null)
				type = type.DeclaringType;
			return type;
		}

		/// <summary>
		/// Compiles the documents, imports the result into <paramref name="module"/> and applies it, like the edit
		/// code commands. Returns false if it didn't compile or import (see the result's errors).
		/// </summary>
		public static bool CompileAndImport(EditResult result, ModuleDef module, IMemberDef? member) {
			if (result.DecompileError is not null)
				return false;
			var typeToEdit = member is null ? null : GetNonNestedType(member as TypeDef ?? member.DeclaringType);
			var tempAssembly = new AssemblyNameInfo {
				HashAlgId = dnlib.DotNet.AssemblyHashAlgorithm.SHA1,
				Version = new Version(0, 0, 0, 0),
				Attributes = AssemblyAttributes.None,
				Name = Guid.NewGuid().ToString(),
				Culture = string.Empty,
			};
			if (!PublicKeyBase.IsNullOrEmpty2(module.Assembly?.PublicKeyOrToken)) {
				tempAssembly.PublicKeyOrToken = new PublicKey(PublicKeyData);
				tempAssembly.Attributes |= AssemblyAttributes.PublicKey;
			}
			var rawModuleBytesProvider = (RawModuleBytesProvider)Activator.CreateInstance(typeof(RawModuleBytesProviderImpl), nonPublic: true)!;
			var referenceResolver = new AssemblyReferenceResolver(rawModuleBytesProvider, module.Context.AssemblyResolver, tempAssembly, module, typeToEdit);
			try {
				return CompileAndImport(result, module, member, typeToEdit, tempAssembly, referenceResolver);
			}
			finally {
				referenceResolver.Dispose();
			}
		}

		static bool CompileAndImport(EditResult result, ModuleDef module, IMemberDef? member, TypeDef? typeToEdit, AssemblyNameInfo tempAssembly, AssemblyReferenceResolver referenceResolver) {
			var references = new List<MetadataReference>();
			foreach (var m in new MetadataReferenceFinder(module, CancellationToken.None).Find(Array.Empty<string>())) {
				var mdRef = m.IsManifestModule ? referenceResolver.Create(m.Assembly) : referenceResolver.Create(m);
				if (mdRef is CompilerMetadataReference r)
					references.Add(CreateMetadataReference(r));
			}

			var compiled = Compile(result, tempAssembly, references, module);
			if (compiled is null)
				return false;

			using var asmResolver = new ModuleImporterAssemblyResolver(referenceResolver.GetReferences());
			var importer = new ModuleImporter(module, asmResolver);
			try {
				var debugFile = new DebugFileResult(DebugFileFormat.PortablePdb, compiled.Value.pdb);
				switch (result.Kind) {
				case EditKind.EditMethod:
					importer.Import(compiled.Value.pe, debugFile, (MethodDef)member!);
					break;
				case EditKind.EditClass:
					importer.Import(compiled.Value.pe, debugFile, typeToEdit!);
					break;
				case EditKind.AddMembers:
					importer.ImportNewMembers(compiled.Value.pe, debugFile, typeToEdit!);
					break;
				case EditKind.EditAssembly:
					importer.Import(compiled.Value.pe, debugFile, ModuleImporterOptions.ReplaceModuleAssemblyAttributes | ModuleImporterOptions.ReplaceAssemblyDeclSecurities | ModuleImporterOptions.ReplaceExportedTypes);
					break;
				}
			}
			catch (ModuleImporterAbortedException) {
			}
			catch (Exception ex) {
				result.ImportErrors.Add("Exception: " + ex);
				return false;
			}
			foreach (var d in importer.Diagnostics.Where(a => a.Severity == CompilerDiagnosticSeverity.Error))
				result.ImportErrors.Add($"{d.Id} {d.Description}");
			if (result.ImportErrors.Count != 0)
				return false;
			Apply(module, importer);
			return true;
		}

		static byte[] PublicKeyData => (byte[])typeof(EditCodeVM).GetField("publicKeyData", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;

		/// <summary>
		/// RoslynLanguageCompiler: C# latest with the TRACE symbol, a netmodule (assembly for Edit Assembly), optimized,
		/// unsafe code allowed (retried without it), errors about [Experimental] APIs suppressed
		/// </summary>
		static (byte[] pe, byte[] pdb)? Compile(EditResult result, AssemblyNameInfo tempAssembly, List<MetadataReference> references, ModuleDef module) {
			var parseOptions = new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols: new[] { "TRACE" });
			var trees = result.Documents.Select(d => CSharpSyntaxTree.ParseText(SourceText.From(d.Text, System.Text.Encoding.UTF8), parseOptions, path: d.Name)).ToArray();
			var options = new CSharpCompilationOptions(result.Kind == EditKind.EditAssembly ? OutputKind.DynamicallyLinkedLibrary : OutputKind.NetModule,
				optimizationLevel: OptimizationLevel.Release, checkOverflow: false, allowUnsafe: true)
				.WithPlatform(GetPlatform(module))
				.WithAssemblyIdentityComparer(DesktopAssemblyIdentityComparer.Default);
			if (tempAssembly.PublicKeyOrToken is PublicKey publicKey)
				options = options.WithCryptoPublicKey(ImmutableArray.Create(publicKey.Data)).WithDelaySign(true);
			Compilation compilation = CSharpCompilation.Create(tempAssembly.Name, trees, references, options);
			var emitted = Emit(compilation, out var errors);
			// RoslynLanguageCompiler.SuppressExperimentalApiErrors
			var experimentalIds = errors.Where(a => a.IsWarningAsError && a.Descriptor.CustomTags.Contains(WellKnownDiagnosticTags.CustomObsolete)).Select(a => a.Id).Distinct().ToArray();
			if (emitted is null && experimentalIds.Length != 0) {
				options = options.WithSpecificDiagnosticOptions(options.SpecificDiagnosticOptions.SetItems(experimentalIds.Select(a => new KeyValuePair<string, ReportDiagnostic>(a, ReportDiagnostic.Suppress))));
				compilation = compilation.WithOptions(options);
				emitted = Emit(compilation, out errors);
			}
			if (emitted is null) {
				var noUnsafe = compilation.WithOptions(options.WithAllowUnsafe(false));
				emitted = Emit(noUnsafe, out var errors2);
				if (emitted is not null)
					return emitted;
			}
			if (emitted is null)
				result.CompileErrors.AddRange(errors.Select(CompileError.Create));
			return emitted;
		}

		static (byte[] pe, byte[] pdb)? Emit(Compilation compilation, out List<Diagnostic> errors) {
			using var pe = new MemoryStream();
			using var pdb = new MemoryStream();
			var emitResult = compilation.Emit(pe, pdb, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
			errors = emitResult.Diagnostics.Where(a => a.Severity == DiagnosticSeverity.Error).ToList();
			if (!emitResult.Success)
				return null;
			return (pe.ToArray(), pdb.ToArray());
		}

		static Platform GetPlatform(ModuleDef module) {
			var platform = PlatformHelper.GetPlatform(module);
			return platform switch {
				TargetPlatform.X86 => Platform.X86,
				TargetPlatform.X64 => Platform.X64,
				TargetPlatform.Arm => Platform.Arm,
				TargetPlatform.Arm64 => Platform.Arm64,
				TargetPlatform.Itanium => Platform.Itanium,
				_ => Platform.AnyCpu,
			};
		}

		/// <summary>dnSpy.Roslyn's CompilerMetadataReference.CreateMetadataReference() (no documentation provider)</summary>
		static unsafe MetadataReference CreateMetadataReference(in CompilerMetadataReference mdRef) {
			var moduleMetadata = ModuleMetadata.CreateFromImage((IntPtr)mdRef.Data, mdRef.Size);
			if (mdRef.IsAssemblyReference)
				return AssemblyMetadata.Create(moduleMetadata).GetReference(filePath: mdRef.Filename);
			return moduleMetadata.GetReference(filePath: mdRef.Filename);
		}

		/// <summary>
		/// What the commands do to the module with the importer's result (AddUpdatedNodesHelper,
		/// ExistingTypeNodeUpdater and the node creators/updaters), without the tree nodes
		/// </summary>
		static void Apply(ModuleDef module, ModuleImporter importer) {
			foreach (var t in importer.NewNonNestedTypes)
				module.Types.Add(t.TargetType!);
			foreach (var t in importer.MergedNonNestedTypes)
				ApplyMerged(module, t);
			var asm = module.Assembly;
			if (asm is not null && importer.NewAssemblyDeclSecurities is DeclSecurity[] declSecurities) {
				asm.DeclSecurities.Clear();
				foreach (var ds in declSecurities)
					asm.DeclSecurities.Add(ds);
			}
			if (asm is not null && importer.NewAssemblyCustomAttributes is CustomAttribute[] asmAttributes) {
				asm.CustomAttributes.Clear();
				foreach (var ca in asmAttributes)
					asm.CustomAttributes.Add(ca);
			}
			if (importer.NewModuleCustomAttributes is CustomAttribute[] modAttributes) {
				module.CustomAttributes.Clear();
				foreach (var ca in modAttributes)
					module.CustomAttributes.Add(ca);
			}
			if (importer.NewExportedTypes is ExportedType[] exportedTypes) {
				module.ExportedTypes.Clear();
				foreach (var et in exportedTypes)
					module.ExportedTypes.Add(et);
			}
			if (asm is not null && importer.NewAssemblyVersion is Version version)
				asm.Version = version;
			foreach (var resource in importer.NewResources ?? Array.Empty<Resource>())
				module.Resources.Add(resource);
		}

		static void ApplyMerged(ModuleDef module, MergedImportedType type) {
			var target = type.TargetType!;
			type.NewTypeDefOptions.CopyTo(target, module);
			foreach (var t in type.DeletedNestedTypes) {
				if (t.DeclaringType is TypeDef owner)
					owner.NestedTypes.Remove(t);
				else
					module.Types.Remove(t);
			}
			foreach (var f in type.DeletedFields)
				target.Fields.Remove(f);
			foreach (var m in type.DeletedMethods)
				target.Methods.Remove(m);
			foreach (var p in type.DeletedProperties)
				target.Properties.Remove(p);
			foreach (var e in type.DeletedEvents)
				target.Events.Remove(e);
			foreach (var nested in type.NewOrExistingNestedTypes.OfType<MergedImportedType>())
				ApplyMerged(module, nested);
			foreach (var nested in type.NewOrExistingNestedTypes.OfType<NewImportedType>())
				target.NestedTypes.Add(nested.TargetType!);
			foreach (var f in type.NewFields)
				target.Fields.Add(f);
			var special = new HashSet<MethodDef>();
			foreach (var p in type.NewProperties)
				special.UnionWith(p.GetMethods.Concat(p.SetMethods).Concat(p.OtherMethods));
			foreach (var e in type.NewEvents)
				special.UnionWith(GetMethods(e));
			foreach (var m in type.NewMethods.Where(a => !special.Contains(a)))
				target.Methods.Add(m);
			foreach (var e in type.NewEvents) {
				target.Events.Add(e);
				foreach (var m in GetMethods(e))
					target.Methods.Add(m);
			}
			foreach (var p in type.NewProperties) {
				target.Properties.Add(p);
				foreach (var m in p.GetMethods.Concat(p.SetMethods).Concat(p.OtherMethods))
					target.Methods.Add(m);
			}
			foreach (var f in type.EditedFields)
				f.FieldDefOptions.CopyTo(f.OriginalField);
			foreach (var m in type.EditedMethods) {
				m.OriginalMethod.MethodBody = m.NewBody;
				m.MethodDefOptions.CopyTo(m.OriginalMethod);
			}
			foreach (var p in type.EditedProperties)
				p.PropertyDefOptions.CopyTo(p.OriginalProperty);
			foreach (var e in type.EditedEvents)
				e.EventDefOptions.CopyTo(e.OriginalEvent);
		}

		static IEnumerable<MethodDef> GetMethods(EventDef e) {
			if (e.AddMethod is not null)
				yield return e.AddMethod;
			if (e.RemoveMethod is not null)
				yield return e.RemoveMethod;
			if (e.InvokeMethod is not null)
				yield return e.InvokeMethod;
			foreach (var m in e.OtherMethods)
				yield return m;
		}

		/// <summary>EditCodeVM.ReferenceDecompilerOutput: finds the caret position (the edited member's definition)</summary>
		sealed class ReferenceDecompilerOutput : StringBuilderDecompilerOutput, IDecompilerOutput {
			readonly object reference;
			public CaretSpan? Span { get; private set; }

			public ReferenceDecompilerOutput(object reference) => this.reference = reference;

			public override void Write(string text, object? reference, DecompilerReferenceFlags flags, object color) =>
				Write(text, 0, text.Length, reference, flags, color);

			public override void Write(string text, int index, int length, object? reference, DecompilerReferenceFlags flags, object color) {
				if (reference == this.reference && (flags & DecompilerReferenceFlags.Definition) != 0 && Span is null) {
					int start = NextPosition;
					base.Write(text, index, length, reference, flags, color);
					Span = new CaretSpan(start, Length - start);
				}
				else
					base.Write(text, index, length, reference, flags, color);
			}

			bool IDecompilerOutput.UsesCustomData => true;
			void IDecompilerOutput.AddCustomData<TData>(string id, TData data) { }
		}
	}
}
