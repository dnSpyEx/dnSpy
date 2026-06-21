using System.Windows.Threading;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Decompiler;
using dnSpy.Contracts.App;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP;

public sealed class DnSpyBridge
{
    public IDocumentTreeView TreeView { get; }
    public IDsDocumentService DocumentService { get; }
    public IDecompilerService DecompilerService { get; }
    public IAppWindow AppWindow { get; }

    private readonly Dispatcher _dispatcher;

    public DnSpyBridge(
        IDocumentTreeView treeView,
        IDsDocumentService documentService,
        IDecompilerService decompilerService,
        IAppWindow appWindow)
    {
        TreeView = treeView;
        DocumentService = documentService;
        DecompilerService = decompilerService;
        AppWindow = appWindow;
        _dispatcher = System.Windows.Application.Current.Dispatcher;
    }

    /// <summary>Run action on UI thread and return result (for dnSpy service calls)</summary>
    public T RunOnUiThread<T>(Func<T> func)
    {
        if (_dispatcher.CheckAccess())
            return func();
        return _dispatcher.Invoke(func);
    }

    /// <summary>Run action on UI thread (fire-and-forget safe)</summary>
    public void RunOnUiThread(Action action)
    {
        if (_dispatcher.CheckAccess())
            action();
        else
            _dispatcher.Invoke(action);
    }

    /// <summary>Get all loaded assemblies (ModuleDef from dnlib)</summary>
    public IReadOnlyList<ModuleDef> GetLoadedModules()
    {
        return RunOnUiThread(() =>
            DocumentService.GetDocuments()
                .Select(d => d.ModuleDef)
                .OfType<ModuleDef>()
                .ToList()
        );
    }

    /// <summary>Find a specific module by name</summary>
    public ModuleDef? FindModule(string name)
    {
        return GetLoadedModules()
            .FirstOrDefault(m =>
                string.Equals(m.Name?.String, name, StringComparison.OrdinalIgnoreCase) ||
                (m.Location ?? "").EndsWith(name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Decompile a type to C# using dnSpy's built-in ILSpy engine</summary>
    public string DecompileType(TypeDef typeDef)
    {
        return RunOnUiThread(() =>
        {
            var decompiler = DecompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            decompiler.Decompile(typeDef, output, new DecompilationContext());
            return output.ToString();
        });
    }

    /// <summary>Decompile a method to C#</summary>
    public string DecompileMethod(MethodDef methodDef)
    {
        return RunOnUiThread(() =>
        {
            var decompiler = DecompilerService.Decompiler;
            var output = new StringBuilderDecompilerOutput();
            decompiler.Decompile(methodDef, output, new DecompilationContext());
            return output.ToString();
        });
    }

    public void Log(string message)
    {
        System.Diagnostics.Debug.WriteLine(message);
    }
}
