using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class StringTools
{
    /// <summary>Extract strings from the #US (user strings) heap</summary>
    [McpServerTool, Description("Extract user strings from .NET #US heap with optional filtering")]
    public static object GetStrings(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Substring filter (case-insensitive)")] string? filter = null,
        [Description("Minimum string length")] int min_length = 4,
        [Description("Max results")] int limit = 500)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var strings = new List<object>();
        var us = (module as ModuleDefMD)?.USStream;
        if (us != null)
        {
            // The #US heap is a sequence of blobs. Each blob is:
            //   [compressed-uint length][UTF-16LE bytes][1 terminator byte]
            // where `length` counts the UTF-16 bytes + the terminator. The first byte
            // of the heap is a null blob (length 0), so iteration starts at offset 1.
            var reader = us.CreateReader();
            reader.Position = 1;
            while (reader.Position < reader.Length && strings.Count < limit)
            {
                uint blobStart = reader.Position;
                if (!reader.TryReadCompressedUInt32(out uint blobLen)) break;
                if (blobLen == 0) continue; // null blob, reader already advanced past the 0x00
                if (reader.Position + blobLen > reader.Length) break;

                // blob payload = (blobLen - 1) UTF-16 bytes + 1 terminator byte
                uint charByteCount = blobLen - 1;
                byte[] bytes = reader.ReadBytes((int)charByteCount);
                reader.ReadByte(); // terminator

                string s = Encoding.Unicode.GetString(bytes);
                if (s.Length < min_length) continue;
                if (filter != null && !s.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;

                strings.Add(new
                {
                    source = "user_strings",
                    offset = $"0x{blobStart:X}",
                    value = s,
                    length = s.Length,
                });
            }
        }

        // Also scan literal string fields
        foreach (var type in module.GetTypes())
        {
            foreach (var field in type.Fields)
            {
                if (field.IsLiteral && field.Constant?.Value is string val && val.Length >= min_length)
                {
                    if (filter == null || val.Contains(filter, StringComparison.OrdinalIgnoreCase))
                    {
                        strings.Add(new
                        {
                            source = "literal_field",
                            declaring_type = type.FullName,
                            field_name = field.Name?.String,
                            value = val,
                            length = val.Length,
                        });
                    }
                }
                if (strings.Count >= limit) break;
            }
        }

        return new { total = strings.Count, strings };
    }
}
