using ModelContextProtocol.Server;
using System.ComponentModel;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class AssemblyTools
{
    /// <summary>List all assemblies currently loaded in dnSpy</summary>
    [McpServerTool, Description("List all .NET assemblies loaded in dnSpy")]
    public static object GetLoadedAssemblies(DnSpyBridge bridge)
    {
        var modules = bridge.GetLoadedModules();
        return modules.Select(m => new
        {
            name = m.Name?.String,
            full_path = m.Location,
            runtime_version = m.RuntimeVersion,
            is_dotnet = true,
            num_types = m.Types?.Count ?? 0,
            has_entry_point = m.EntryPoint != null,
            mvid = m.Mvid?.ToString(),
        }).ToList();
    }

    /// <summary>Get detailed metadata for a specific assembly</summary>
    [McpServerTool, Description("Get detailed assembly metadata: CLR header, streams, PE sections, assembly references")]
    public static object GetAssemblyInfo(
        DnSpyBridge bridge,
        [Description("Assembly filename (e.g., 'stealer.exe')")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        return new
        {
            name = module.Name?.String,
            location = module.Location,
            runtime_version = module.RuntimeVersion,
            entry_point = module.EntryPoint?.FullName,
            mvid = module.Mvid?.ToString(),
            cor20_header_flags = module.Cor20HeaderFlags.ToString(),
            assembly_refs = module.GetAssemblyRefs().Select(r => new
            {
                name = r.Name?.String,
                version = r.Version?.ToString(),
                public_key_token = r.PublicKeyOrToken?.ToString(),
            }).ToList(),
            module_refs = module.GetModuleRefs().Select(r => r.Name?.String).ToList(),
            num_typedefs = module.Types?.Count ?? 0,
            num_typerefs = module.GetTypeRefs().Count(),
            num_memberrefs = module.GetMemberRefs().Count(),
        };
    }
}
