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
using dnlib.DotNet.Resources;
using dnSpy.Contracts.Decompiler;
using dnSpy.Decompiler.ILSpy.Tests.Infrastructure;
using Xunit;
using Xunit.Abstractions;

namespace dnSpy.Decompiler.ILSpy.Tests {
	/// <summary>
	/// The BAML decompiler (XAML of WPF assemblies). Its connection ID pass reads IComponentConnector.Connect and
	/// IStyleConnector.Connect to add x:Name and event handler attributes.
	/// </summary>
	public class BamlTests {
		readonly ITestOutputHelper log;
		public BamlTests(ITestOutputHelper log) => this.log = log;

		internal static IBamlDecompiler Decompiler => bamlDecompiler.Value;
		static readonly Lazy<IBamlDecompiler> bamlDecompiler = new Lazy<IBamlDecompiler>(() => {
			// Same as dnSpy.Console
			var type = System.Reflection.Assembly.Load("dnSpy.BamlDecompiler.x").GetType("dnSpy.BamlDecompiler.BamlDecompiler", throwOnError: true)!;
			return (IBamlDecompiler)Activator.CreateInstance(type)!;
		});

		/// <summary>
		/// A module context that finds the WPF assemblies (on Linux they're only in the NuGet cache)
		/// </summary>
		/// <param name="includeRuntime">Also find the runtime's assemblies (on Linux dnlib doesn't find them by itself).
		/// Not for WPF: the runtime has its own WindowsBase.dll (a facade without WPF's types) with the version WPF
		/// assemblies reference, which dnlib prefers.</param>
		internal static ModuleContext CreateModuleContext(bool includeRuntime = false) {
			var context = ModuleDef.CreateModuleContext();
			var resolver = (AssemblyResolver)context.AssemblyResolver;
			foreach (var dir in GetWpfDirectories())
				resolver.PostSearchPaths.Add(dir);
			if (includeRuntime)
				resolver.PostSearchPaths.Add(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory());
			return context;
		}

		static IEnumerable<string> GetWpfDirectories() {
			// Windows: the shared framework
			var programFiles = Environment.GetEnvironmentVariable("ProgramFiles");
			if (programFiles is not null) {
				var shared = Path.Combine(programFiles, "dotnet", "shared", "Microsoft.WindowsDesktop.App");
				if (Directory.Exists(shared)) {
					var latest = Directory.GetDirectories(shared).OrderByDescending(a => Version.TryParse(Path.GetFileName(a).Split('-')[0], out var v) ? v : new Version()).FirstOrDefault();
					if (latest is not null && File.Exists(Path.Combine(latest, "PresentationFramework.dll"))) {
						yield return latest;
						yield break;
					}
				}
			}
			var nugetDir = Environment.GetEnvironmentVariable("NUGET_PACKAGES") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
			foreach (var pack in new[] { "microsoft.windowsdesktop.app.runtime.win-x64", "microsoft.windowsdesktop.app.ref" }) {
				var packDir = Path.Combine(nugetDir, pack);
				if (!Directory.Exists(packDir))
					continue;
				foreach (var dir in Directory.GetDirectories(packDir, "*", SearchOption.AllDirectories).OrderByDescending(a => a, StringComparer.Ordinal)) {
					if (File.Exists(Path.Combine(dir, "PresentationFramework.dll"))) {
						yield return dir;
						yield break;
					}
				}
			}
		}

		/// <summary>
		/// The BAML resources of a module (name, data)
		/// </summary>
		internal static IEnumerable<(string name, byte[] data)> GetBamlResources(ModuleDef module) {
			foreach (var er in module.Resources.OfType<EmbeddedResource>()) {
				if (!er.Name.String.EndsWith(".g.resources", StringComparison.OrdinalIgnoreCase) || !ResourceReader.CouldBeResourcesFile(er.CreateReader()))
					continue;
				ResourceElementSet set;
				try {
					set = ResourceReader.Read(module, er.CreateReader());
				}
				catch (ResourceReaderException) {
					continue;
				}
				foreach (var e in set.ResourceElements) {
					if (!e.Name.EndsWith(".baml", StringComparison.OrdinalIgnoreCase))
						continue;
					if (e.ResourceData is BuiltInResourceData data && data.Data is byte[] bytes)
						yield return (e.Name, bytes);
				}
			}
		}

		internal static string DecompileBaml(ModuleDef module, byte[] data) {
			var output = new MemoryStream();
			var options = BamlDecompilerOptions.CreateCSharp();
			bamlDecompiler.Value.Decompile(module, data, CancellationToken.None, options, output, new XamlOutputOptions { IndentChars = "\t", NewLineChars = "\n" });
			return Encoding.UTF8.GetString(output.ToArray());
		}

		/// <summary>
		/// Comments the connection ID pass adds when it can't do its job
		/// </summary>
		static readonly string[] connectionIdProblems = {
			"cannot be parsed",
			"Unknown connection ID",
		};

		static IEnumerable<string> GetProblems(string xaml) => connectionIdProblems.Where(a => xaml.Contains(a, StringComparison.Ordinal));

		/// <summary>
		/// dnSpy's own dialogs (dnSpy.AsmEditor is built with the tests): the connection ID pass (the upstream decompiler's
		/// ILAst of IComponentConnector.Connect) finds the names and event handlers of the elements
		/// </summary>
		[Fact]
		public void ConnectionIds_DnSpyDialogs() {
			var file = typeof(dnSpy.AsmEditor.Compiler.ModuleImporter).Assembly.Location;
			var module = ModuleDefMD.Load(file, CreateModuleContext());
			var documents = GetBamlResources(module).ToList();
			Assert.True(documents.Count > 50, documents.Count.ToString());
			int handlers = 0, names = 0;
			string? editCodeDialog = null;
			foreach (var (name, data) in documents) {
				var xaml = DecompileBaml(module, data);
				Assert.False(xaml.StartsWith("EXCEPTION", StringComparison.Ordinal), name);
				Assert.Empty(GetProblems(xaml));
				handlers += CountOccurrences(xaml, "Click=\"");
				names += CountOccurrences(xaml, " Name=\"");
				if (name.EndsWith("editcodedlg.baml", StringComparison.OrdinalIgnoreCase))
					editCodeDialog = xaml;
			}
			log.WriteLine($"{documents.Count} documents, {handlers} click handlers, {names} names");
			Assert.True(handlers >= 30, handlers.ToString());
			Assert.True(names >= 500, names.ToString());
			Assert.NotNull(editCodeDialog);
			Assert.Contains("<ListView Name=\"diagnosticsListView\"", editCodeDialog);
			Assert.Contains("MouseDoubleClick=\"diagnosticsListView_MouseDoubleClick\"", editCodeDialog);
			Assert.Contains("<ProgressBar Name=\"progressBar\"", editCodeDialog);
		}

		static int CountOccurrences(string text, string value) {
			int count = 0;
			for (int index = 0; (index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0; index += value.Length)
				count++;
			return count;
		}

		/// <summary>
		/// .NET's Fluent theme (PresentationFramework.Fluent): the root declares 'sys' for System in mscorlib, an element
		/// of a merged dictionary declares 'sys' for System in System.Runtime and is itself mscorlib's 'Double'. The XML
		/// writer threw ("The prefix 'sys' cannot be redefined ... within the same start element tag"); the element gets
		/// its own prefix for mscorlib.
		/// </summary>
		[Fact]
		public void XmlnsConflicts_AreWritten() {
			XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
			XNamespace mscorlib = "clr-namespace:System;assembly=mscorlib";
			XNamespace runtime = "clr-namespace:System;assembly=System.Runtime";
			var doc = new XDocument(new XElement(presentation + "ResourceDictionary",
				new XAttribute("xmlns", presentation.NamespaceName),
				new XAttribute(XNamespace.Xmlns + "sys", mscorlib.NamespaceName),
				new XElement(mscorlib + "Double", new XAttribute(XNamespace.Xmlns + "sys", runtime.NamespaceName), "12"),
				new XElement(presentation + "Grid", new XAttribute(XNamespace.Xmlns + "sys", runtime.NamespaceName),
					new XElement(mscorlib + "String", "a"),
					new XElement(runtime + "Int32", "1")),
				new XElement(mscorlib + "Boolean", "True")));
			// (Internal, like the decompiler itself, which dnSpy.Console loads by name)
			var passType = System.Reflection.Assembly.Load("dnSpy.BamlDecompiler.x").GetType("dnSpy.BamlDecompiler.Rewrite.XmlnsConflictRewritePass", throwOnError: true)!;
			passType.GetMethod("Run")!.Invoke(Activator.CreateInstance(passType), new object?[] { null, doc });
			var xaml = doc.ToString();
			log.WriteLine(xaml);
			// Same names, and the prefixes mean what they meant before
			var read = XDocument.Parse(xaml);
			Assert.Equal(doc.Descendants().Select(a => a.Name), read.Descendants().Select(a => a.Name));
			Assert.Contains("<sys1:Double", xaml);
			Assert.Contains("<sys1:String", xaml);
			Assert.Contains("<sys:Int32", xaml);
			Assert.Contains("<sys:Boolean", xaml);
		}

		const string ApplicationProgram = """
			using System;
			namespace System.Windows {
				public class StartupEventArgs : EventArgs { }
				public delegate void StartupEventHandler(object sender, StartupEventArgs e);
				public class Application {
					public event StartupEventHandler Startup;
					public event EventHandler Exit;
					public Uri StartupUri { get; set; }
					public static void LoadComponent(object component, Uri resourceLocator) { }
				}
			}
			namespace AppTest {
				// What the XAML compiler generates for <Application x:Class="AppTest.App" Startup="App_Startup" Exit="OnExit" StartupUri="Views/MainWindow.xaml">
				public class App : System.Windows.Application {
					bool _contentLoaded;
					public void InitializeComponent() {
						if (_contentLoaded)
							return;
						_contentLoaded = true;
						this.Startup += new System.Windows.StartupEventHandler(this.App_Startup);
						this.Exit += new System.EventHandler(this.OnExit);
						this.StartupUri = new System.Uri("Views/MainWindow.xaml", System.UriKind.Relative);
						System.Uri resourceLocater = new System.Uri("/AppTest;component/app.xaml", System.UriKind.Relative);
						System.Windows.Application.LoadComponent(this, resourceLocater);
					}
					void App_Startup(object sender, System.Windows.StartupEventArgs e) { }
					protected virtual void OnExit(object sender, EventArgs e) { }
				}
			}
			""";

		/// <summary>
		/// App.xaml's StartupUri and event handlers are code in InitializeComponent (not the resource locator's URI)
		/// </summary>
		[Theory]
		[InlineData(false)]
		[InlineData(true)]
		public void ApplicationXaml_AttributesFromInitializeComponent(bool optimize) {
			var module = TestCompiler.Compile("AppTest", ApplicationProgram, optimize).Load();
			var method = module.Find("AppTest.App", false).FindMethod("InitializeComponent");
			var passType = System.Reflection.Assembly.Load("dnSpy.BamlDecompiler.x").GetType("dnSpy.BamlDecompiler.Rewrite.ApplicationRewritePass", throwOnError: true)!;
			var result = passType.GetMethod("GetAttributes", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { method });
			var attributes = ((System.Collections.IEnumerable)result!).Cast<object>().Select(a => a.ToString()).ToArray();
			Assert.Equal(new[] { "(Startup, App_Startup)", "(Exit, OnExit)", "(StartupUri, Views/MainWindow.xaml)" }, attributes);
		}

		/// <summary>
		/// Opt-in (BAML_CORPUS=dir): the XAML of every BAML resource of the assemblies in the directory. BAML_CORPUS_OUT=dir
		/// saves the XAML (to compare two versions of the decompiler).
		/// </summary>
		[Fact]
		public void DecompileBaml_Corpus() {
			var dir = Environment.GetEnvironmentVariable("BAML_CORPUS");
			if (dir is null)
				return;
			var outDir = Environment.GetEnvironmentVariable("BAML_CORPUS_OUT");
			int total = 0, withProblems = 0, connections = 0;
			var watch = System.Diagnostics.Stopwatch.StartNew();
			// One context like dnSpy: the referenced assemblies are loaded once
			var context = CreateModuleContext();
			int skipped = 0;
			foreach (var file in Directory.GetFiles(dir, "*.dll").OrderBy(a => a, StringComparer.Ordinal)) {
				ModuleDefMD module;
				try {
					module = ModuleDefMD.Load(file, context);
				}
				catch (BadImageFormatException) {
					// A native DLL (the WPF framework folder has some)
					skipped++;
					continue;
				}
				foreach (var (name, data) in GetBamlResources(module)) {
					total++;
					string xaml;
					try {
						xaml = DecompileBaml(module, data);
					}
					catch (Exception ex) {
						xaml = "EXCEPTION: " + ex;
					}
					var problems = GetProblems(xaml).ToList();
					if (problems.Count != 0 || xaml.StartsWith("EXCEPTION", StringComparison.Ordinal)) {
						withProblems++;
						log.WriteLine($"{Path.GetFileName(file)} {name}: {string.Join(", ", problems)} {(xaml.StartsWith("EXCEPTION", StringComparison.Ordinal) ? xaml.Substring(0, Math.Min(300, xaml.Length)) : "")}");
					}
					if (xaml.Contains("x:Name=", StringComparison.Ordinal) || xaml.Contains("Click=", StringComparison.Ordinal))
						connections++;
					if (outDir is not null) {
						var path = Path.Combine(outDir, Path.GetFileNameWithoutExtension(file), name.Replace('/', '_') + ".xaml");
						Directory.CreateDirectory(Path.GetDirectoryName(path)!);
						File.WriteAllText(path, xaml);
					}
				}
			}
			log.WriteLine($"BAML: {total} documents, {withProblems} with connection ID problems, {connections} with names or handlers, {skipped} native DLLs skipped ({watch.Elapsed.TotalSeconds:F1} s)");
		}
	}
}
