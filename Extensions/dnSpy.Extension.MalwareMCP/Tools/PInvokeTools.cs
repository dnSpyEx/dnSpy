using ModelContextProtocol.Server;
using System.ComponentModel;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class PInvokeTools
{
    /// <summary>List all P/Invoke (DllImport) declarations</summary>
    [McpServerTool, Description("List all P/Invoke declarations (DllImport) — native API calls")]
    public static object GetPInvokes(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var pinvokes = new List<object>();
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (method.ImplMap == null) continue;
                var impl = method.ImplMap;
                pinvokes.Add(new
                {
                    method_name = method.Name?.String,
                    declaring_type = type.FullName,
                    dll_name = impl.Module?.Name?.String,
                    entry_point = impl.Name?.String,
                    calling_convention = impl.Attributes.ToString(),
                });
            }
        }

        return new { count = pinvokes.Count, pinvokes };
    }
}
