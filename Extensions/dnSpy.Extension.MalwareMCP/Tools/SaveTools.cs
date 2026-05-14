using ModelContextProtocol.Server;
using System.ComponentModel;
using System.IO;
using dnlib.DotNet;
using dnlib.DotNet.Writer;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class SaveTools
{
    /// <summary>Save a modified assembly to disk after deobfuscation/renaming</summary>
    [McpServerTool, Description("Save a modified assembly to disk (e.g., after renaming/deobfuscation). Writes to a new file alongside the original. Requires confirm=true.")]
    public static object SaveAssembly(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Output path (if empty, saves as '<original>_modified.exe/dll')")] string? output_path = null,
        [Description("Must be true to execute")] bool confirm = false)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        // Generate default output path
        if (string.IsNullOrEmpty(output_path))
        {
            var ext = Path.GetExtension(module.Location);
            var dir = Path.GetDirectoryName(module.Location) ?? ".";
            var baseName = Path.GetFileNameWithoutExtension(module.Location);
            output_path = Path.Combine(dir, $"{baseName}_modified{ext}");
        }

        if (!confirm)
        {
            return new
            {
                status = "dry_run",
                message = $"Would save modified assembly to: {output_path}. Set confirm=true to execute.",
                source = module.Location,
                destination = output_path,
            };
        }

        try
        {
            // Use appropriate writer options
            if (module.IsILOnly || module is not ModuleDefMD md)
            {
                var options = new ModuleWriterOptions(module);
                options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                module.Write(output_path, options);
            }
            else
            {
                var options = new NativeModuleWriterOptions(md, optimizeImageSize: false);
                options.MetadataOptions.Flags |= MetadataFlags.PreserveAll;
                md.NativeWrite(output_path, options);
            }

            var fileInfo = new FileInfo(output_path);
            return new
            {
                status = "saved",
                output_path,
                file_size = fileInfo.Length,
                original_path = module.Location,
            };
        }
        catch (Exception ex)
        {
            return new
            {
                status = "error",
                message = ex.Message,
                output_path,
            };
        }
    }
}
