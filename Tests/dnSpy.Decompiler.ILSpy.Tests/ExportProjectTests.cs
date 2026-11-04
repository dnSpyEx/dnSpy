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
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;
using dnlib.DotNet;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.MSBuild;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Microsoft.CodeAnalysis.Text;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// Export to Project: dnSpy's project writer with the upstream decompiler. The projects are compiled like MSBuild
	/// would (the project file's LangVersion, AllowUnsafeBlocks, Nullable) and the programs must behave the same.
	/// </summary>
	public class ExportProjectTests {
		readonly ITestOutputHelper log;
		public ExportProjectTests(ITestOutputHelper log) => this.log = log;

		sealed class Logger : IMSBuildProjectWriterLogger {
			public List<string> Errors { get; } = new List<string>();
			public void Error(string message) {
				lock (Errors)
					Errors.Add(message);
			}
		}

		internal sealed class ExportedProject {
			public string ProjectFile { get; }
			/// <summary>
			/// Files the project writer couldn't create
			/// </summary>
			public List<string> Errors { get; } = new List<string>();
			public XDocument Xml { get; }
			public List<string> SourceFiles { get; } = new List<string>();
			public ExportedProject(string projectFile) {
				ProjectFile = projectFile;
				Xml = XDocument.Load(projectFile);
				var dir = Path.GetDirectoryName(projectFile)!;
				foreach (var e in Xml.Descendants().Where(a => a.Name.LocalName == "Compile")) {
					var include = e.Attribute("Include")?.Value;
					if (include is not null)
						SourceFiles.Add(Path.Combine(dir, include.Replace('\\', Path.DirectorySeparatorChar)));
				}
			}

			public string? GetProperty(string name) => Xml.Descendants().FirstOrDefault(a => a.Name.LocalName == name)?.Value;
		}

		/// <summary>
		/// Exports a module to a project in a new directory
		/// </summary>
		internal static ExportedProject Export(ModuleDef module, IDecompiler decompiler, bool sdkStyle, string? directory = null, bool decompileXaml = false) {
			directory ??= Path.Combine(Path.GetTempPath(), "dnspy-export-" + Guid.NewGuid().ToString("N"));
			Directory.CreateDirectory(directory);
			var ctx = new DecompilationContext { AsyncMethodBodyDecompilation = false };
			var logger = new Logger();
			var options = new ProjectCreatorOptions(directory, CancellationToken.None) {
				Logger = logger,
				GenerateSDKStyleProjects = sdkStyle,
				NumberOfThreads = 4,
				CreateDecompilerOutput = writer => new TextWriterDecompilerOutput(writer, new dnSpy.Contracts.Text.Indenter(4, 4, false)),
			};
			var moduleOptions = new ProjectModuleOptions(module, decompiler, ctx);
			if (decompileXaml) {
				// Like dnSpy.Console
				var bamlOptions = BamlDecompilerOptions.Create(decompiler);
				var outputOptions = new XamlOutputOptions { IndentChars = "\t", NewLineChars = "\n", NewLineOnAttributes = true };
				moduleOptions.DecompileXaml = true;
				moduleOptions.DecompileBamlTypeName = (a, b, c) => BamlTests.Decompiler.DecompileTypeName(a, b, c, bamlOptions);
				moduleOptions.DecompileBaml = (a, b, c, d) => BamlTests.Decompiler.Decompile(a, b, c, bamlOptions, d, outputOptions);
			}
			options.ProjectModules.Add(moduleOptions);
			new MSBuildProjectCreator(options).Create();
			var projectFile = Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories).Single();
			var project = new ExportedProject(projectFile);
			project.Errors.AddRange(logger.Errors);
			return project;
		}

		/// <summary>
		/// Compiles the source files of an exported project like MSBuild would
		/// </summary>
		internal static (byte[]? image, List<Diagnostic> errors) Compile(ExportedProject project, string assemblyName, IEnumerable<MetadataReference> references, bool executable = false,
			System.Threading.CancellationToken cancellationToken = default, byte[]? publicKey = null) {
			var langVersion = project.GetProperty("LangVersion");
			LanguageVersion languageVersion = LanguageVersion.Default;
			if (langVersion is not null)
				Assert.True(LanguageVersionFacts.TryParse(langVersion, out languageVersion), "LangVersion " + langVersion);
			var parseOptions = new CSharpParseOptions(languageVersion);
			var trees = project.SourceFiles.Select(f => CSharpSyntaxTree.ParseText(SourceText.From(File.ReadAllText(f), Encoding.UTF8), parseOptions, path: f)).ToArray();
			var nullable = project.GetProperty("Nullable") switch {
				"enable" => NullableContextOptions.Enable,
				"annotations" => NullableContextOptions.Annotations,
				"warnings" => NullableContextOptions.Warnings,
				_ => NullableContextOptions.Disable,
			};
			var options = new CSharpCompilationOptions(executable ? OutputKind.ConsoleApplication : OutputKind.DynamicallyLinkedLibrary,
				allowUnsafe: string.Equals(project.GetProperty("AllowUnsafeBlocks"), "true", StringComparison.OrdinalIgnoreCase),
				nullableContextOptions: nullable);
			// Friend assemblies: the assembly's public key (public signing) so InternalsVisibleTo applies
			if (publicKey is not null && publicKey.Length != 0)
				options = options.WithCryptoPublicKey(System.Collections.Immutable.ImmutableArray.Create(publicKey)).WithPublicSign(true);
			var noWarn = (project.GetProperty("NoWarn") ?? "").Split(';', ',').Select(a => a.Trim()).Where(a => a.Length != 0 && !a.StartsWith("$(", StringComparison.Ordinal)).ToArray();
			if (noWarn.Length != 0)
				options = options.WithSpecificDiagnosticOptions(noWarn.Select(a => new KeyValuePair<string, ReportDiagnostic>(a, ReportDiagnostic.Suppress)));
			var compilation = CSharpCompilation.Create(assemblyName, trees, references, options);
			using var pe = new MemoryStream();
			var result = compilation.Emit(pe, cancellationToken: cancellationToken);
			var errors = result.Diagnostics.Where(a => a.Severity == DiagnosticSeverity.Error).ToList();
			return (result.Success ? pe.ToArray() : null, errors);
		}

		static string Describe(IEnumerable<Diagnostic> errors, int max = 10) =>
			string.Join(Environment.NewLine, errors.Take(max).Select(a => $"{a.Id} {Path.GetFileName(a.Location.SourceTree?.FilePath)}{a.Location.GetLineSpan().StartLinePosition}: {a.GetMessage()} [{GetLine(a)}]"));

		static string GetLine(Diagnostic d) {
			var tree = d.Location.SourceTree;
			if (tree is null)
				return "";
			var line = tree.GetText().Lines[d.Location.GetLineSpan().StartLinePosition.Line].ToString().Trim();
			return line.Length > 120 ? line.Substring(0, 120) : line;
		}

		/// <summary>
		/// The project writer gets the target framework from the assembly (the test programs are compiled without one)
		/// </summary>
		internal static string WithTargetFramework(string source) {
			int index = source.IndexOf("namespace ", StringComparison.Ordinal);
			return source.Insert(index, "[assembly: System.Runtime.Versioning.TargetFramework(\".NETCoreApp,Version=v10.0\")]\n");
		}

		/// <summary>
		/// The C# 1-10 test program: exported, compiled from the project and run, it prints the same as the original
		/// </summary>
		[Theory]
		[InlineData(true)]
		[InlineData(false)]
		public void Classic_ExportCompileRun_SameBehavior(bool sdkStyle) {
			var original = TestCompiler.Compile("EditClassic", WithTargetFramework(EditPrograms.Classic));
			var expected = ProgramRunner.Run(original.Image, "EditClassic");
			Assert.False(expected.StartsWith("EXCEPTION", StringComparison.Ordinal) || expected.StartsWith("CRASH", StringComparison.Ordinal), expected);
			var project = Export(original.Load(), LanguageRunner.Create(), sdkStyle);
			log.WriteLine($"{Path.GetFileName(project.ProjectFile)}: {project.SourceFiles.Count} files, LangVersion {project.GetProperty("LangVersion")}, Nullable {project.GetProperty("Nullable")}");
			Assert.Empty(project.Errors);
			var (image, errors) = Compile(project, "EditClassic", TestCompiler.References);
			Assert.True(image is not null, Describe(errors));
			Assert.Equal(expected, ProgramRunner.Run(image!, "EditClassic"));
		}

		/// <summary>
		/// A classic project file can't target .NET Core / .NET 5+ or .NET Standard (Visual Studio shows it as
		/// "unloaded"), so those get an SDK-style project even if SDK-style is off. .NET Framework keeps the classic one.
		/// </summary>
		[Theory]
		[InlineData(".NETCoreApp,Version=v10.0", true)]
		[InlineData(".NETStandard,Version=v2.0", true)]
		[InlineData(".NETFramework,Version=v4.8", false)]
		public void ClassicExport_DotNetCoreOrStandard_IsSdkStyle(string targetFramework, bool sdkStyleProject) {
			const string source = "namespace ExportTfm { public static class Program { public static int Answer() => 42; } }";
			var original = TestCompiler.Compile("ExportTfm", $"[assembly: System.Runtime.Versioning.TargetFramework(\"{targetFramework}\")]\n" + source);
			var project = Export(original.Load(), LanguageRunner.Create(), sdkStyle: false);
			Assert.Empty(project.Errors);
			var xml = File.ReadAllText(project.ProjectFile);
			Assert.Equal(sdkStyleProject, xml.Contains("<Project Sdk=", StringComparison.Ordinal));
			Assert.Equal(!sdkStyleProject, xml.Contains("<TargetFrameworkVersion>", StringComparison.Ordinal));
		}

		/// <summary>
		/// A library as built for an older framework (its own NotNullWhen, IsExternalInit and Index, an [Experimental]
		/// API it calls, nullable annotations): exported, compiled and run, it prints the same
		/// </summary>
		[Fact]
		public void Compat_ExportCompileRun_SameBehavior() {
			var original = TestCompiler.Compile("EditCompat", WithTargetFramework(EditPrograms.Compat));
			var expected = ProgramRunner.Run(original.Image, "EditCompat");
			var project = Export(original.Load(), LanguageRunner.Create(), sdkStyle: true);
			Assert.Contains("COMPAT001", project.GetProperty("NoWarn") ?? "");
			var (image, errors) = Compile(project, "EditCompat", TestCompiler.References);
			Assert.True(image is not null, Describe(errors));
			Assert.Equal(expected, ProgramRunner.Run(image!, "EditCompat"));
		}

		/// <summary>
		/// The C# 11-14 program: the code compiles except where upstream 11.2 can't decompile it (file-local types and
		/// lambdas with default or params parameters, which use the compiler's anonymous delegate types)
		/// </summary>
		[Fact]
		public void Modern_ExportCompile_OnlyKnownUpstreamLimitations() {
			var original = TestCompiler.Compile("EditModern", WithTargetFramework(EditPrograms.Modern));
			var project = Export(original.Load(), LanguageRunner.Create(), sdkStyle: true);
			Assert.Equal("14.0", project.GetProperty("LangVersion"));
			var (_, errors) = Compile(project, "EditModern", TestCompiler.References);
			var unexpected = errors.Where(a => !IsKnownUpstreamLimitation(a)).ToList();
			Assert.True(unexpected.Count == 0, Describe(unexpected));
			// C# 14 compound assignment operators are declared as operators
			var counter = File.ReadAllText(project.SourceFiles.Single(a => Path.GetFileName(a) == "Counter.cs"));
			Assert.Contains("public void operator +=(int n)", counter);
			Assert.Contains("public void operator ++()", counter);
			Assert.DoesNotContain("SpecialName", counter);
			Assert.DoesNotContain("CompilerFeatureRequired", counter);
		}

		static bool IsKnownUpstreamLimitation(Diagnostic error) {
			var file = Path.GetFileName(error.Location.SourceTree?.FilePath ?? "");
			// File-local type '<EditModern>F...__Secret' and its file ('<EditModern>F....cs' on Linux, '-EditModern-F....cs'
			// on Windows, where '<' and '>' can't be in a file name)
			if (System.Text.RegularExpressions.Regex.IsMatch(file, "EditModern.F[0-9A-F]{16,}") || GetLine(error).Contains("__Secret", StringComparison.Ordinal))
				return true;
			// '<>f__AnonymousDelegate0<int, int, int> obj = (int a, int b = 2) => a + b;'
			var line = GetLine(error);
			return line.Contains("f__AnonymousDelegate", StringComparison.Ordinal) || line.Contains("obj(1) + obj2(", StringComparison.Ordinal);
		}

		const string PartialTypesProgram = """
			using System;
			using System.Collections.Generic;
			namespace System.Windows.Markup {
				public interface IComponentConnector {
					void Connect(int connectionId, object target);
					void InitializeComponent();
				}
			}
			namespace PartialTypes {
				public class Control { }
				public class Window1 : Control, System.Windows.Markup.IComponentConnector {
					internal List<int> items;
					private bool _contentLoaded;
					public Window1() => InitializeComponent();
					public void InitializeComponent() {
						if (_contentLoaded)
							return;
						_contentLoaded = true;
					}
					void System.Windows.Markup.IComponentConnector.Connect(int connectionId, object target) {
						if (connectionId == 1)
							items = (List<int>)target;
					}
					void OnClick(object sender, EventArgs e) => Console.WriteLine(items.Count);
					public class Nested { public int Value; }
				}
			}
			""";

		static string DecompilePartial(TypeDef type, Action<DecompilePartialType> configure) {
			var language = LanguageRunner.Create();
			Assert.True(language.CanDecompile(DecompilationType.PartialType));
			var output = new CapturingDecompilerOutput();
			var info = new DecompilePartialType(output, LanguageRunner.CreateContext(), type);
			configure(info);
			language.Decompile(DecompilationType.PartialType, info);
			return output.Text;
		}

		/// <summary>
		/// The code-behind of a XAML file: without the members the XAML compiler generates and its interfaces
		/// </summary>
		[Fact]
		public void PartialType_XamlCodeBehind() {
			var module = TestCompiler.Compile("PartialTypes", PartialTypesProgram).Load();
			var type = module.Find("PartialTypes.Window1", false);
			var text = DecompilePartial(type, info => {
				info.Definitions.Add(type.FindMethod("InitializeComponent"));
				info.Definitions.Add(type.Methods.Single(a => a.Name.EndsWith("Connect")));
				info.Definitions.Add(type.FindField("_contentLoaded"));
				info.Definitions.Add(type.FindField("items"));
				info.InterfacesToRemove.Add(new TypeRefUser(module, "System.Windows.Markup", "IComponentConnector"));
			});
			log.WriteLine(text);
			Assert.Contains("namespace PartialTypes", text);
			Assert.Contains("public partial class Window1 : Control", text);
			Assert.DoesNotContain("IComponentConnector", text);
			Assert.DoesNotContain("void InitializeComponent()", text);
			Assert.DoesNotContain("Connect(", text);
			Assert.DoesNotContain("_contentLoaded", text);
			Assert.DoesNotContain("List<int> items", text);
			// The constructor still calls it, the other members and nested types are there
			Assert.Contains("InitializeComponent();", text);
			Assert.Contains("OnClick", text);
			Assert.Contains("class Nested", text);
		}

		/// <summary>
		/// A WinForms designer file: only the members it lists, with full type names
		/// </summary>
		[Fact]
		public void PartialType_DesignerFile() {
			var module = TestCompiler.Compile("PartialTypes", PartialTypesProgram).Load();
			var type = module.Find("PartialTypes.Window1", false);
			var text = DecompilePartial(type, info => {
				info.ShowDefinitions = true;
				info.UseUsingDeclarations = false;
				info.Definitions.Add(type.FindMethod("InitializeComponent"));
				info.Definitions.Add(type.FindField("_contentLoaded"));
				info.Definitions.Add(type.FindField("items"));
			});
			log.WriteLine(text);
			Assert.Contains("partial class Window1", text);
			Assert.Contains("void InitializeComponent()", text);
			Assert.Contains("System.Collections.Generic.List<int> items", text);
			Assert.DoesNotContain("using System", text);
			Assert.DoesNotContain("OnClick", text);
			Assert.DoesNotContain("class Nested", text);
			Assert.DoesNotContain("Connect(", text);
		}

		/// <summary>
		/// The solution file of two projects, one x86. .slnx (also with an upper case extension): what the .NET SDK writes
		/// for the .sln dnSpy writes ('dotnet sln migrate'): the solution's platforms, '/' in paths, the x86 project's
		/// platform. .sln: Visual Studio 2026's header. The .NET SDK reads both and builds the .slnx.
		/// </summary>
		[Theory]
		[InlineData("Exported.slnx")]
		[InlineData("Exported.SLNX")]
		[InlineData("Exported.sln")]
		public void Solution_SlnAndSlnx(string solutionName) {
			var directory = Path.Combine(Path.GetTempPath(), "dnspy-export-" + Guid.NewGuid().ToString("N"));
			var library = TestCompiler.Compile("SlnLib", WithTargetFramework("namespace SlnLib { public static class C { public static int F() => 1; } }")).Load();
			var x86 = TestCompiler.Compile("SlnX86", WithTargetFramework("namespace SlnX86 { public static class C { public static int F() => 2; } }")).Load();
			x86.Machine = dnlib.PE.Machine.I386;
			x86.Is32BitRequired = true;
			var logger = new Logger();
			var options = new ProjectCreatorOptions(directory, CancellationToken.None) {
				Logger = logger,
				GenerateSDKStyleProjects = true,
				ProjectVersion = ProjectVersion.VS2026,
				SolutionFilename = solutionName,
				CreateDecompilerOutput = writer => new TextWriterDecompilerOutput(writer, new dnSpy.Contracts.Text.Indenter(4, 4, false)),
			};
			foreach (var module in new[] { library, x86 })
				options.ProjectModules.Add(new ProjectModuleOptions(module, LanguageRunner.Create(), new DecompilationContext { AsyncMethodBodyDecompilation = false }));
			new MSBuildProjectCreator(options).Create();
			Assert.Empty(logger.Errors);
			var text = File.ReadAllText(Path.Combine(directory, solutionName));
			log.WriteLine(text);
			if (Path.GetExtension(solutionName) == ".sln")
				Assert.StartsWith("\r\nMicrosoft Visual Studio Solution File, Format Version 12.00\r\n# Visual Studio Version 18\r\nVisualStudioVersion = 18.", text);
			else {
				const string expected = """
					<Solution>
					  <Configurations>
					    <Platform Name="Any CPU" />
					    <Platform Name="Mixed Platforms" />
					    <Platform Name="x86" />
					  </Configurations>
					  <Project Path="SlnLib/SlnLib.csproj" />
					  <Project Path="SlnX86/SlnX86.csproj">
					    <Platform Project="x86" />
					  </Project>
					</Solution>
					""";
				Assert.Equal(XDocument.Parse(expected).ToString(), XDocument.Parse(text).ToString());
			}
			var list = RunDotnet(directory, "sln", solutionName, "list");
			Assert.Contains("SlnLib", list);
			Assert.Contains("SlnX86", list);
			if (Path.GetExtension(solutionName) == ".slnx") {
				// The test programs are compiled against the runtime's System.Private.CoreLib, which the projects then
				// reference; the SDK references the framework
				foreach (var projectFile in Directory.GetFiles(directory, "*.csproj", SearchOption.AllDirectories)) {
					var xml = XDocument.Load(projectFile);
					xml.Descendants().Where(a => a.Name.LocalName == "Reference" && (string?)a.Attribute("Include") == "System.Private.CoreLib").Remove();
					xml.Save(projectFile);
				}
				RunDotnet(directory, "build", solutionName, "-nologo", "-v:q");
				// The x86 project was built for its platform
				Assert.True(Directory.Exists(Path.Combine(directory, "SlnX86", "bin", "x86")), "SlnX86 not built for x86");
				Assert.False(Directory.Exists(Path.Combine(directory, "SlnLib", "bin", "x86")), "SlnLib built for x86");
			}
		}

		string RunDotnet(string directory, params string[] arguments) {
			var psi = new System.Diagnostics.ProcessStartInfo("dotnet") {
				WorkingDirectory = directory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
			};
			foreach (var a in arguments)
				psi.ArgumentList.Add(a);
			psi.Environment["DOTNET_NOLOGO"] = "1";
			psi.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
			using var process = System.Diagnostics.Process.Start(psi)!;
			var stderr = process.StandardError.ReadToEndAsync();
			var stdout = process.StandardOutput.ReadToEnd();
			Assert.True(process.WaitForExit(5 * 60 * 1000), "dotnet " + string.Join(" ", arguments) + " timed out");
			log.WriteLine($"dotnet {string.Join(" ", arguments)}: {stdout}{stderr.Result}");
			Assert.True(process.ExitCode == 0, $"dotnet {string.Join(" ", arguments)}: exit code {process.ExitCode}: {stdout}{stderr.Result}");
			return stdout;
		}

		const string StyleConnectorProgram = """
			using System;
			namespace System.Windows.Markup {
				public interface IComponentConnector {
					void Connect(int connectionId, object target);
					void InitializeComponent();
				}
				public interface IStyleConnector {
					void Connect(int connectionId, object target);
				}
			}
			namespace StyleConnector {
				public class Window1 : System.Windows.Markup.IComponentConnector, System.Windows.Markup.IStyleConnector {
					internal object okButton;
					private bool _contentLoaded;
					public Window1() => InitializeComponent();
					public void InitializeComponent() {
						if (_contentLoaded)
							return;
						_contentLoaded = true;
					}
					void System.Windows.Markup.IComponentConnector.Connect(int connectionId, object target) {
						if (connectionId == 1)
							okButton = target;
					}
					void System.Windows.Markup.IStyleConnector.Connect(int connectionId, object target) {
						if (connectionId == 2)
							Console.WriteLine(target);
					}
					public void Connect(int id, object value) => Console.WriteLine(value);
					void OnClick(object sender, EventArgs e) => Console.WriteLine(okButton);
				}
			}
			""";

		/// <summary>
		/// The members of a XAML code-behind type that the XAML compiler generates: also IStyleConnector.Connect (a style
		/// with an EventSetter), which can't be in the code-behind file twice, but not the type's own Connect method
		/// </summary>
		[Fact]
		public void XamlCodeBehind_GeneratedMembers() {
			var module = TestCompiler.Compile("StyleConnector", StyleConnectorProgram).Load();
			var type = module.Find("StyleConnector.Window1", false);
			var defs = XamlTypeProjectFile.GetDefsToRemove(type).Select(a => a.Name.String).ToList();
			log.WriteLine(string.Join(", ", defs));
			Assert.Contains("InitializeComponent", defs);
			Assert.Contains("_contentLoaded", defs);
			Assert.Contains("System.Windows.Markup.IComponentConnector.Connect", defs);
			Assert.Contains("System.Windows.Markup.IStyleConnector.Connect", defs);
			Assert.Contains("okButton", defs);
			Assert.DoesNotContain("Connect", defs);
			Assert.DoesNotContain("OnClick", defs);
		}

		/// <summary>
		/// SDK-style projects for .NET 5+ target the assembly's platform ("net10.0-windows7.0"; WPF and Windows Forms
		/// projects can't be built without a Windows platform, NETSDK1136)
		/// </summary>
		[Theory]
		[InlineData(null, "net10.0")]
		[InlineData("Windows7.0", "net10.0-windows7.0")]
		[InlineData("Windows10.0.19041.0", "net10.0-windows10.0.19041.0")]
		public void SdkProject_TargetFrameworkPlatform(string? platform, string expected) {
			var source = WithTargetFramework("namespace Platform { public static class Program { public static void Main() { } } }");
			if (platform is not null)
				source = $"[assembly: System.Runtime.Versioning.TargetPlatform(\"{platform}\")]\n" + source;
			var module = TestCompiler.Compile("Platform", source).Load();
			var project = Export(module, LanguageRunner.Create(), sdkStyle: true);
			Assert.Empty(project.Errors);
			Assert.Equal(expected, project.GetProperty("TargetFramework"));
		}

		/// <summary>
		/// dnSpy's own WPF dialogs (dnSpy.AsmEditor): each XAML file's code-behind is the other part of the partial type,
		/// without what the XAML compiler generates (InitializeComponent, Connect, the named elements' fields)
		/// </summary>
		[Fact]
		public void Wpf_CodeBehindFiles() {
			var file = typeof(dnSpy.AsmEditor.Compiler.ModuleImporter).Assembly.Location;
			var module = ModuleDefMD.Load(file, BamlTests.CreateModuleContext());
			var project = Export(module, LanguageRunner.Create(), sdkStyle: true, decompileXaml: true);
			// (The ResX writer needs WinForms, which the test host doesn't have on Linux)
			var errors = project.Errors.Where(a => !a.Contains("Create ResX file", StringComparison.Ordinal) || !a.Contains("System.Windows.Forms", StringComparison.Ordinal)).ToList();
			Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors.Take(3)));
			// A WPF project needs a Windows platform
			Assert.Equal("net10.0-windows7.0", project.GetProperty("TargetFramework"));
			var codeBehind = project.SourceFiles.Where(a => a.EndsWith(".xaml.cs", StringComparison.OrdinalIgnoreCase)).ToList();
			log.WriteLine($"{project.SourceFiles.Count} files, {codeBehind.Count} code-behind files");
			Assert.True(codeBehind.Count >= 40, codeBehind.Count.ToString());
			var problems = new List<string>();
			foreach (var path in codeBehind) {
				var text = File.ReadAllText(path);
				var xaml = path.Substring(0, path.Length - 3);
				if (!File.Exists(xaml) || new FileInfo(xaml).Length == 0)
					problems.Add(Path.GetFileName(path) + ": no XAML file");
				if (!text.Contains(" partial class ", StringComparison.Ordinal))
					problems.Add(Path.GetFileName(path) + ": not partial");
				if (text.Contains("IComponentConnector", StringComparison.Ordinal) || text.Contains("void InitializeComponent()", StringComparison.Ordinal) || text.Contains("_contentLoaded", StringComparison.Ordinal))
					problems.Add(Path.GetFileName(path) + ": has generated code");
			}
			Assert.Empty(problems.Take(10));
		}

		/// <summary>
		/// The Windows checks' WPF app (built with the tests). Its App.xaml has resources, so it's BAML, and the XAML
		/// compiler put its Startup handler in InitializeComponent: the exported App.xaml must have it, or the exported app
		/// doesn't run its startup code (the Windows checks' run timed out). The window keeps its names and handlers.
		/// </summary>
		[Fact]
		public void Wpf_SampleApp() {
			var file = Path.Combine(AppContext.BaseDirectory, "Samples", "WpfSample", "WpfSample.dll");
			var module = ModuleDefMD.Load(file, BamlTests.CreateModuleContext());
			var project = Export(module, LanguageRunner.Create(), sdkStyle: true, decompileXaml: true);
			Assert.True(project.Errors.Count == 0, string.Join(Environment.NewLine, project.Errors));
			Assert.Equal("net10.0-windows7.0", project.GetProperty("TargetFramework"));
			var dir = Path.GetDirectoryName(project.ProjectFile)!;
			var app = File.ReadAllText(Path.Combine(dir, "App.xaml"));
			log.WriteLine(app);
			Assert.Contains("Startup=\"App_Startup\"", app);
			Assert.Contains("x:Key=\"AccentBrush\"", app);
			Assert.DoesNotContain("StartupUri", app);
			var window = File.ReadAllText(Path.Combine(dir, "MainWindow.xaml"));
			foreach (var expected in new[] { "Loaded=\"Window_Loaded\"", "Click=\"OkButton_Click\"", "Click=\"Toolbar_Click\"", "TextChanged=\"NameBox_TextChanged\"", "Handler=\"Item_MouseDoubleClick\"", "Name=\"okButton\"" })
				Assert.Contains(expected, window);
			var codeBehind = File.ReadAllText(Path.Combine(dir, "MainWindow.xaml.cs"));
			Assert.DoesNotContain("Connect(", codeBehind);
			Assert.DoesNotContain("void InitializeComponent()", codeBehind);
		}

		/// <summary>
		/// Opt-in (EXPORT_CORPUS=dir): exports each assembly in the directory and compiles the project's code against the
		/// assembly's references. EXPORT_CORPUS_OUT=dir keeps the projects.
		/// </summary>
		[Fact]
		public void ExportAndCompile_RealWorldAssemblies() {
			var dir = Environment.GetEnvironmentVariable("EXPORT_CORPUS");
			if (dir is null)
				return;
			var outDir = Environment.GetEnvironmentVariable("EXPORT_CORPUS_OUT") ?? Path.Combine(Path.GetTempPath(), "dnspy-export-corpus");
			var engines = new[] { "upstream" };
			// Also saved as it goes (the test's output is only shown at the end)
			Directory.CreateDirectory(outDir);
			var resultsFile = Path.Combine(outDir, "results.txt");
			void Report(string line) {
				log.WriteLine(line);
				File.AppendAllText(resultsFile, line + Environment.NewLine);
			}
			foreach (var file in Directory.GetFiles(dir, "*.dll").OrderBy(a => a, StringComparer.Ordinal)) {
				foreach (var engine in engines) {
					var context = BamlTests.CreateModuleContext(includeRuntime: true);
					var resolver = (AssemblyResolver)context.AssemblyResolver;
					resolver.PreSearchPaths.Insert(0, Path.GetDirectoryName(file)!);
					// (References that aren't part of the corpus)
					if (Directory.Exists(Path.Combine(dir, "refs")))
						resolver.PostSearchPaths.Add(Path.Combine(dir, "refs"));
					var module = ModuleDefMD.Load(file, context);
					resolver.AddToCache(module);
					IDecompiler decompiler = LanguageRunner.Create();
					var projectDir = Path.Combine(outDir, engine, Path.GetFileNameWithoutExtension(file));
					if (Directory.Exists(projectDir))
						Directory.Delete(projectDir, recursive: true);
					var watch = System.Diagnostics.Stopwatch.StartNew();
					ExportedProject project;
					try {
						project = Export(module, decompiler, sdkStyle: true, projectDir);
					}
					catch (Exception ex) {
						Report($"{engine} {Path.GetFileName(file)}: export failed: {ex.GetType().Name}: {ex.Message}");
						continue;
					}
					double exportSeconds = watch.Elapsed.TotalSeconds;
					var references = GetReferences(module);
					List<Diagnostic> errors;
					// (The compiler can take very long on code with lots of errors)
					using (var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMinutes(5))) {
						try {
							errors = Compile(project, module.Assembly?.Name ?? "Exported", references, cancellationToken: timeout.Token, publicKey: module.Assembly?.PublicKey?.Data).errors;
						}
						catch (OperationCanceledException) {
							Report($"{engine} {Path.GetFileName(file)}: {project.SourceFiles.Count} files ({exportSeconds:F0} s), the compiler didn't finish in 5 minutes");
							continue;
						}
					}
					var byId = errors.GroupBy(a => a.Id).OrderByDescending(a => a.Count()).Take(8).Select(a => $"{a.Key} {a.Count()}");
					var files = errors.Select(a => a.Location.SourceTree?.FilePath).Distinct().Count();
					Report($"{engine} {Path.GetFileName(file)}: {project.SourceFiles.Count} files, {errors.Count} errors in {files} files ({exportSeconds:F0} s), LangVersion {project.GetProperty("LangVersion")}: {string.Join(", ", byId)}");
					if (errors.Count != 0) {
						var byFile = errors.GroupBy(a => Path.GetFileName(a.Location.SourceTree?.FilePath) ?? "?").OrderByDescending(a => a.Count()).Take(12).Select(a => $"{a.Key} {a.Count()}");
						Report("  Files: " + string.Join(", ", byFile));
					}
					if (errors.Count != 0)
						Report("  " + Describe(errors, 6).Replace(Environment.NewLine, Environment.NewLine + "  "));
					module.Dispose();
				}
			}
		}

		/// <summary>
		/// The assemblies the module references (and the runtime's)
		/// </summary>
		static List<MetadataReference> GetReferences(ModuleDef module) {
			var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			var resolver = module.Context.AssemblyResolver;
			var queue = new Queue<ModuleDef>();
			queue.Enqueue(module);
			var seen = new HashSet<ModuleDef> { module };
			while (queue.Count > 0) {
				var m = queue.Dequeue();
				foreach (var asmRef in m.GetAssemblyRefs()) {
					var asm = resolver.Resolve(asmRef, m);
					if (asm?.ManifestModule is not ModuleDef refModule || string.IsNullOrEmpty(refModule.Location))
						continue;
					if (paths.Add(refModule.Location) && seen.Add(refModule))
						queue.Enqueue(refModule);
				}
			}
			var references = paths.Select(a => (MetadataReference)MetadataReference.CreateFromFile(a)).ToList();
			// The compiler needs the core library even if the module's references don't resolve to it
			var names = new HashSet<string>(paths.Select(Path.GetFileName)!, StringComparer.OrdinalIgnoreCase);
			foreach (var reference in TestCompiler.References.OfType<PortableExecutableReference>()) {
				if (reference.FilePath is string path && !names.Contains(Path.GetFileName(path)) && Path.GetFileName(path) is "netstandard.dll" or "mscorlib.dll" or "System.Runtime.dll" or "System.Private.CoreLib.dll")
					references.Add(reference);
			}
			return references;
		}
	}
}
