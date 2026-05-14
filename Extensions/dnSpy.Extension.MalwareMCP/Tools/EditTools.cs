using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class EditTools
{
    /// <summary>Rename a type (class/struct/etc)</summary>
    [McpServerTool, Description("Rename a type. Requires confirm=true. Changes are visible in dnSpy immediately.")]
    public static object RenameType(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Current full type name")] string old_name,
        [Description("New type name (just the name, not namespace)")] string new_name,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!confirm)
            return new { status = "dry_run", message = $"Would rename '{old_name}' to '{new_name}'. Set confirm=true to execute." };

        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var type = module.GetTypes().FirstOrDefault(t => t.FullName == old_name)
            ?? throw new ArgumentException($"Type not found: {old_name}");

        bridge.RunOnUiThread(() => { type.Name = new UTF8String(new_name); });

        return new { status = "renamed", old_name, new_name = type.FullName };
    }

    /// <summary>Rename a method</summary>
    [McpServerTool, Description("Rename a method. Requires confirm=true.")]
    public static object RenameMethod(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (Type::Method)")] string old_name,
        [Description("New method name")] string new_name,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!confirm)
            return new { status = "dry_run", message = $"Would rename method '{old_name}' to '{new_name}'. Set confirm=true to execute." };

        var (type, method) = MethodTools.ResolveMethod(bridge, assembly_name, old_name);

        bridge.RunOnUiThread(() => { method.Name = new UTF8String(new_name); });

        return new { status = "renamed", old_name, new_full_name = method.FullName };
    }

    /// <summary>Rename a field</summary>
    [McpServerTool, Description("Rename a field. Requires confirm=true.")]
    public static object RenameField(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name containing the field")] string type_full_name,
        [Description("Current field name")] string old_name,
        [Description("New field name")] string new_name,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!confirm)
            return new { status = "dry_run", message = $"Would rename field '{old_name}' to '{new_name}'. Set confirm=true to execute." };

        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var type = module.GetTypes().FirstOrDefault(t => t.FullName == type_full_name)
            ?? throw new ArgumentException($"Type not found: {type_full_name}");

        var field = type.Fields.FirstOrDefault(f => f.Name?.String == old_name)
            ?? throw new ArgumentException($"Field not found: {old_name} in {type_full_name}");

        bridge.RunOnUiThread(() => { field.Name = new UTF8String(new_name); });

        return new { status = "renamed", old_name, new_name, declaring_type = type.FullName };
    }

    /// <summary>Batch rename multiple methods at once (for deobfuscation)</summary>
    [McpServerTool, Description("Batch rename methods/types for deobfuscation. Requires confirm=true.")]
    public static object BatchRename(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("JSON array of {old_name, new_name} pairs")] string renames_json,
        [Description("Must be true to execute")] bool confirm = false)
    {
        var renames = JsonSerializer.Deserialize<List<RenameEntry>>(renames_json)
            ?? throw new ArgumentException("Invalid JSON");

        if (!confirm)
            return new { status = "dry_run", count = renames.Count, message = "Set confirm=true to execute." };

        var results = new List<object>();
        bridge.RunOnUiThread(() =>
        {
            var module = bridge.FindModule(assembly_name)!;
            foreach (var r in renames)
            {
                try
                {
                    // Try as method first (Type::Method format)
                    if (r.old_name.Contains("::"))
                    {
                        var parts = r.old_name.Split("::", 2);
                        var type = module.GetTypes().FirstOrDefault(t => t.FullName == parts[0]);
                        var method = type?.Methods.FirstOrDefault(m => m.Name == parts[1]);
                        if (method != null)
                        {
                            method.Name = new UTF8String(r.new_name);
                            results.Add(new { old = r.old_name, @new = r.new_name, status = "ok" });
                            continue;
                        }
                    }
                    // Try as type
                    var t2 = module.GetTypes().FirstOrDefault(t => t.FullName == r.old_name);
                    if (t2 != null)
                    {
                        t2.Name = new UTF8String(r.new_name);
                        results.Add(new { old = r.old_name, @new = r.new_name, status = "ok" });
                    }
                    else
                    {
                        results.Add(new { old = r.old_name, @new = r.new_name, status = "not_found" });
                    }
                }
                catch (Exception ex)
                {
                    results.Add(new { old = r.old_name, @new = r.new_name, status = $"error: {ex.Message}" });
                }
            }
        });

        return new { renamed = results.Count(r => ((dynamic)r).status == "ok"), total = renames.Count, results };
    }

    record RenameEntry(string old_name, string new_name);
}
