using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class ResourceTools
{
    /// <summary>List all resources in an assembly</summary>
    [McpServerTool, Description("List all embedded resources with size and entropy information")]
    public static object ListResources(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var resources = new List<object>();
        if (module.Resources != null)
        {
            foreach (var r in module.Resources)
            {
                long size = 0;
                double entropy = 0;

                if (r is EmbeddedResource er)
                {
                    var data = er.CreateReader().ToArray();
                    size = data.Length;
                    entropy = Utils.Entropy.Calculate(data);
                }

                resources.Add(new
                {
                    name = r.Name?.String,
                    resource_type = r.ResourceType.ToString(),
                    size,
                    entropy = Math.Round(entropy, 2),
                    is_high_entropy = entropy > 7.0,
                });
            }
        }

        return new { count = resources.Count, resources };
    }

    /// <summary>Extract raw bytes from an embedded resource (base64 encoded)</summary>
    [McpServerTool, Description("Extract embedded resource content as base64. Use for encrypted/packed payloads.")]
    public static object ExtractResource(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Resource name")] string resource_name,
        [Description("Max bytes to extract (default 65536)")] int max_bytes = 65536)
    {
        var module = bridge.FindModule(assembly_name);
        if (module == null)
            return new { error = $"Assembly not found: {assembly_name}", resource_name };

        var embedded = module.Resources?.OfType<EmbeddedResource>().ToList() ?? new List<EmbeddedResource>();
        if (embedded.Count == 0)
            return new { error = "Assembly has no embedded resources", assembly = assembly_name, resource_name };

        var resource = embedded.FirstOrDefault(r => r.Name?.String == resource_name);
        if (resource == null)
            return new
            {
                error = $"Embedded resource not found: {resource_name}",
                assembly = assembly_name,
                available = embedded.Select(r => r.Name?.String).ToList(),
            };

        var data = resource.CreateReader().ToArray();
        var extractSize = Math.Min(data.Length, max_bytes);
        var extracted = new byte[extractSize];
        Array.Copy(data, extracted, extractSize);

        return new
        {
            name = resource_name,
            total_size = data.Length,
            extracted_size = extractSize,
            truncated = data.Length > max_bytes,
            entropy = Math.Round(Utils.Entropy.Calculate(data), 2),
            data_base64 = Convert.ToBase64String(extracted),
            // First 16 bytes as hex for quick identification
            header_hex = BitConverter.ToString(data, 0, Math.Min(16, data.Length)).Replace("-", " "),
        };
    }
}
