using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class SearchTools
{
    /// <summary>Regex search across type, method, field, and property names</summary>
    [McpServerTool, Description("Search for types, methods, fields, and properties by regex pattern across an assembly. Essential for finding items in obfuscated code.")]
    public static object SearchCode(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Regex pattern to match against names")] string pattern,
        [Description("What to search: types, methods, fields, properties, all")] string scope = "all",
        [Description("Max results")] int limit = 200)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);
        var results = new List<object>();

        foreach (var type in module.GetTypes())
        {
            if (results.Count >= limit) break;

            if (scope is "types" or "all" &&
                (regex.IsMatch(type.Name?.String ?? "") || regex.IsMatch(type.FullName ?? "")))
            {
                results.Add(new
                {
                    kind = "type",
                    token = $"0x{type.MDToken.Raw:X8}",
                    name = type.Name?.String,
                    full_name = type.FullName,
                    @namespace = type.Namespace?.String,
                });
            }

            if (scope is "methods" or "all")
            {
                foreach (var m in type.Methods)
                {
                    if (results.Count >= limit) break;
                    if (regex.IsMatch(m.Name?.String ?? "") || regex.IsMatch(m.FullName ?? ""))
                    {
                        results.Add(new
                        {
                            kind = "method",
                            token = $"0x{m.MDToken.Raw:X8}",
                            name = m.Name?.String,
                            full_name = m.FullName,
                            declaring_type = type.FullName,
                            return_type = m.ReturnType?.FullName,
                            is_static = m.IsStatic,
                        });
                    }
                }
            }

            if (scope is "fields" or "all")
            {
                foreach (var f in type.Fields)
                {
                    if (results.Count >= limit) break;
                    if (regex.IsMatch(f.Name?.String ?? "") || regex.IsMatch(f.FullName ?? ""))
                    {
                        results.Add(new
                        {
                            kind = "field",
                            token = $"0x{f.MDToken.Raw:X8}",
                            name = f.Name?.String,
                            declaring_type = type.FullName,
                            field_type = f.FieldType?.FullName,
                            is_static = f.IsStatic,
                            constant = f.IsLiteral ? f.Constant?.Value?.ToString() : null,
                        });
                    }
                }
            }

            if (scope is "properties" or "all")
            {
                foreach (var p in type.Properties)
                {
                    if (results.Count >= limit) break;
                    if (regex.IsMatch(p.Name?.String ?? "") || regex.IsMatch(p.FullName ?? ""))
                    {
                        results.Add(new
                        {
                            kind = "property",
                            name = p.Name?.String,
                            declaring_type = type.FullName,
                            property_type = p.PropertySig?.RetType?.FullName,
                        });
                    }
                }
            }
        }

        return new { pattern, scope, total = results.Count, results };
    }

    /// <summary>Find all methods that reference a specific string via ldstr instruction</summary>
    [McpServerTool, Description("Find all methods that reference a specific string (ldstr). Critical for tracing C2 URLs, encryption keys, mutex names, file paths.")]
    public static object FindStringReferences(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("String to search for (exact or substring)")] string search_string,
        [Description("Match mode: exact, contains, regex")] string match_mode = "contains",
        [Description("Max results")] int limit = 200)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        Regex? regex = match_mode == "regex" ? new Regex(search_string, RegexOptions.IgnoreCase | RegexOptions.Compiled) : null;
        var results = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || results.Count >= limit) continue;

                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.OpCode != OpCodes.Ldstr || instr.Operand is not string strValue)
                        continue;

                    bool matches = match_mode switch
                    {
                        "exact" => strValue.Equals(search_string, StringComparison.OrdinalIgnoreCase),
                        "contains" => strValue.Contains(search_string, StringComparison.OrdinalIgnoreCase),
                        "regex" => regex!.IsMatch(strValue),
                        _ => strValue.Contains(search_string, StringComparison.OrdinalIgnoreCase),
                    };

                    if (matches)
                    {
                        results.Add(new
                        {
                            method_full_name = method.FullName,
                            declaring_type = type.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            string_value = strValue,
                            method_token = $"0x{method.MDToken.Raw:X8}",
                        });
                    }
                }
            }
        }

        return new { search_string, match_mode, total = results.Count, results };
    }

    /// <summary>Find methods containing specific IL opcode sequences or operand patterns</summary>
    [McpServerTool, Description("Search for methods containing specific IL opcode/operand patterns. Use to find crypto operations, string decryption, API calls by pattern.")]
    public static object SearchILPattern(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Comma-separated opcode names to match in sequence (e.g., 'ldsfld,ldarg.0,call' or 'xor' or 'callvirt')")] string opcodes,
        [Description("Optional regex to match against operand text")] string? operand_pattern = null,
        [Description("Max results")] int limit = 100)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var opcodeList = opcodes.Split(',').Select(o => o.Trim().ToLowerInvariant()).ToArray();
        Regex? operandRegex = operand_pattern != null
            ? new Regex(operand_pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled)
            : null;

        var results = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || results.Count >= limit) continue;

                var instrs = method.Body.Instructions;
                var matches = new List<object>();

                // Sliding window search for opcode sequence
                for (int i = 0; i <= instrs.Count - opcodeList.Length; i++)
                {
                    bool seqMatch = true;
                    for (int j = 0; j < opcodeList.Length; j++)
                    {
                        if (instrs[i + j].OpCode.Name.ToLowerInvariant() != opcodeList[j])
                        {
                            seqMatch = false;
                            break;
                        }
                    }

                    if (!seqMatch) continue;

                    // Check operand pattern if specified
                    if (operandRegex != null)
                    {
                        bool operandMatch = false;
                        for (int j = 0; j < opcodeList.Length; j++)
                        {
                            var operandStr = MethodTools.FormatOperand(instrs[i + j]);
                            if (operandRegex.IsMatch(operandStr))
                            {
                                operandMatch = true;
                                break;
                            }
                        }
                        if (!operandMatch) continue;
                    }

                    matches.Add(new
                    {
                        offset = $"IL_{instrs[i].Offset:X4}",
                        sequence = Enumerable.Range(0, opcodeList.Length).Select(j => new
                        {
                            opcode = instrs[i + j].OpCode.Name,
                            operand = MethodTools.FormatOperand(instrs[i + j]),
                        }).ToList(),
                    });
                }

                if (matches.Count > 0)
                {
                    results.Add(new
                    {
                        method_full_name = method.FullName,
                        declaring_type = type.FullName,
                        method_token = $"0x{method.MDToken.Raw:X8}",
                        match_count = matches.Count,
                        matches,
                    });
                }
            }
        }

        return new { opcodes, operand_pattern, total_methods = results.Count, results };
    }
}
