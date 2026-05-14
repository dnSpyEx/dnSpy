using System.ComponentModel.Composition;
using System.IO;
using dnSpy.Contracts.App;
using dnSpy.Contracts.Documents;
using dnSpy.Contracts.Documents.TreeView;
using dnSpy.Contracts.Decompiler;

namespace dnSpy.Extension.MalwareMCP;

[ExportAutoLoaded]
sealed class McpExtensionLoader : IAutoLoaded
{
    private readonly McpServerHost _host;

    [ImportingConstructor]
    McpExtensionLoader(
        IDocumentTreeView documentTreeView,
        IDsDocumentService documentService,
        IDecompilerService decompilerService,
        IAppWindow appWindow)
    {
        Diagnostics.Log("[Loader] Constructor invoked");
        try
        {
            var bridge = new DnSpyBridge(documentTreeView, documentService, decompilerService, appWindow);
            _host = new McpServerHost(bridge);
            Diagnostics.Log("[Loader] Starting MCP host on thread-pool thread...");
            // Start on a thread-pool thread so the WPF dispatcher SynchronizationContext
            // from this UI-thread constructor isn't captured by any awaiter inside
            // ASP.NET Core/Kestrel. Tool calls still marshal to the UI thread explicitly
            // via DnSpyBridge.RunOnUiThread when they need dnSpy services.
            Task.Run(async () =>
            {
                try { await _host.StartAsync().ConfigureAwait(false); }
                catch (Exception ex) { Diagnostics.Log($"[Loader] StartAsync task FAILED: {ex}"); }
            });
        }
        catch (Exception ex)
        {
            Diagnostics.Log($"[Loader] FATAL: {ex}");
            throw;
        }
    }
}

[ExportExtension]
public sealed class MalwareMcpExtension : IExtension
{
    public void OnEvent(ExtensionEvent @event, object? obj) { }

    public ExtensionInfo ExtensionInfo => new()
    {
        ShortDescription = "MCP Server for AI-assisted .NET malware analysis",
    };

    public IEnumerable<string> MergedResourceDictionaries { get; } = [];
}

internal static class Diagnostics
{
    private static readonly string LogPath = Path.Combine(
        Path.GetDirectoryName(typeof(Diagnostics).Assembly.Location) ?? ".",
        "malware-mcp.log");
    private static readonly object _lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (_lock)
            {
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
        }
        catch { /* never throw from logger */ }
    }
}
