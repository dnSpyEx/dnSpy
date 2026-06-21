using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class AttributeTools
{
    /// <summary>List custom attributes with their constructor arguments</summary>
    [McpServerTool, Description("List all custom attributes on assembly, types, and methods with their constructor arguments. Useful for detecting obfuscator watermarks, serialization settings, COM visibility, and other metadata.")]
    public static object GetCustomAttributes(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Scope: assembly, types, methods, all")] string scope = "all",
        [Description("Optional attribute name filter (substring)")] string? filter = null,
        [Description("Max results")] int limit = 500)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var results = new List<object>();

        // Assembly-level attributes
        if (scope is "assembly" or "all" && module.Assembly?.CustomAttributes != null)
        {
            foreach (var attr in module.Assembly.CustomAttributes)
            {
                if (filter != null && !(attr.TypeFullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                    continue;
                if (results.Count >= limit) break;

                results.Add(new
                {
                    target = "assembly",
                    target_name = module.Assembly.FullName,
                    attribute_type = attr.TypeFullName,
                    constructor_args = FormatAttributeArgs(attr),
                    named_args = FormatNamedArgs(attr),
                });
            }
        }

        // Module-level attributes
        if (scope is "assembly" or "all" && module.CustomAttributes != null)
        {
            foreach (var attr in module.CustomAttributes)
            {
                if (filter != null && !(attr.TypeFullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                    continue;
                if (results.Count >= limit) break;

                results.Add(new
                {
                    target = "module",
                    target_name = module.Name?.String,
                    attribute_type = attr.TypeFullName,
                    constructor_args = FormatAttributeArgs(attr),
                    named_args = FormatNamedArgs(attr),
                });
            }
        }

        // Type-level and method-level attributes
        if (scope is "types" or "methods" or "all")
        {
            foreach (var type in module.GetTypes())
            {
                if (results.Count >= limit) break;

                if (scope is "types" or "all" && type.CustomAttributes != null)
                {
                    foreach (var attr in type.CustomAttributes)
                    {
                        if (filter != null && !(attr.TypeFullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                            continue;
                        if (results.Count >= limit) break;

                        results.Add(new
                        {
                            target = "type",
                            target_name = type.FullName,
                            attribute_type = attr.TypeFullName,
                            constructor_args = FormatAttributeArgs(attr),
                            named_args = FormatNamedArgs(attr),
                        });
                    }
                }

                if (scope is "methods" or "all")
                {
                    foreach (var method in type.Methods)
                    {
                        if (method.CustomAttributes == null) continue;
                        foreach (var attr in method.CustomAttributes)
                        {
                            if (filter != null && !(attr.TypeFullName?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true))
                                continue;
                            if (results.Count >= limit) break;

                            results.Add(new
                            {
                                target = "method",
                                target_name = method.FullName,
                                attribute_type = attr.TypeFullName,
                                constructor_args = FormatAttributeArgs(attr),
                                named_args = FormatNamedArgs(attr),
                            });
                        }
                    }
                }
            }
        }

        return new { total = results.Count, attributes = results };
    }

    /// <summary>Analyze all static constructors (.cctor) in the assembly</summary>
    [McpServerTool, Description("Analyze all static constructors (.cctor) in the assembly. Static constructors run before any type member is accessed — malware uses them for initialization, decryption, unpacking, anti-debug checks, and setting up C2 config.")]
    public static object AnalyzeStaticConstructors(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var cctors = new List<object>();

        // Check <Module>::.cctor first (module initializer — runs before everything)
        var moduleType = module.Types.FirstOrDefault(t => t.Name == "<Module>");
        var moduleCctor = moduleType?.FindStaticConstructor();
        if (moduleCctor != null)
        {
            cctors.Add(AnalyzeCctor(moduleCctor, moduleType!, isModuleInit: true));
        }

        // Then all other type .cctors
        foreach (var type in module.GetTypes())
        {
            if (type.Name == "<Module>") continue;
            var cctor = type.FindStaticConstructor();
            if (cctor == null) continue;

            cctors.Add(AnalyzeCctor(cctor, type, isModuleInit: false));
        }

        return new
        {
            assembly = assembly_name,
            total_cctors = cctors.Count,
            has_module_initializer = moduleCctor != null,
            static_constructors = cctors,
        };
    }

    private static object AnalyzeCctor(MethodDef cctor, TypeDef type, bool isModuleInit)
    {
        var calls = new List<object>();
        var strings = new List<string>();
        var fieldWrites = new List<string>();
        var suspiciousOps = new List<string>();

        if (cctor.HasBody)
        {
            foreach (var instr in cctor.Body.Instructions)
            {
                // Track method calls
                if (instr.Operand is IMethod calledMethod &&
                    (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt ||
                     instr.OpCode == OpCodes.Newobj))
                {
                    var calledName = calledMethod.FullName ?? "";
                    calls.Add(new
                    {
                        il_offset = $"IL_{instr.Offset:X4}",
                        target = calledName,
                        opcode = instr.OpCode.Name,
                    });

                    // Flag suspicious patterns in cctors
                    if (calledName.Contains("Assembly::Load", StringComparison.OrdinalIgnoreCase))
                        suspiciousOps.Add("Assembly loading in .cctor");
                    if (calledName.Contains("WebClient") || calledName.Contains("HttpClient"))
                        suspiciousOps.Add("Network access in .cctor");
                    if (calledName.Contains("Process.Start"))
                        suspiciousOps.Add("Process creation in .cctor");
                    if (calledName.Contains("Thread") && calledName.Contains(".Start"))
                        suspiciousOps.Add("Thread creation in .cctor");
                    if (calledName.Contains("Debugger"))
                        suspiciousOps.Add("Debugger detection in .cctor");
                }

                // Track string literals
                if (instr.OpCode == OpCodes.Ldstr && instr.Operand is string str)
                    strings.Add(str);

                // Track field writes (initialization)
                if ((instr.OpCode == OpCodes.Stsfld || instr.OpCode == OpCodes.Stfld) &&
                    instr.Operand is IField field)
                    fieldWrites.Add(field.FullName);
            }
        }

        return new
        {
            type = type.FullName,
            is_module_initializer = isModuleInit,
            token = $"0x{cctor.MDToken.Raw:X8}",
            il_size = cctor.Body?.Instructions?.Count ?? 0,
            calls_count = calls.Count,
            calls,
            strings_used = strings,
            fields_initialized = fieldWrites,
            suspicious_operations = suspiciousOps,
            risk_level = suspiciousOps.Count > 0 ? "high" : calls.Count > 5 ? "medium" : "low",
        };
    }

    private static List<object> FormatAttributeArgs(CustomAttribute attr)
    {
        return attr.ConstructorArguments?.Select(a => (object)new
        {
            type = a.Type?.FullName,
            value = a.Value?.ToString(),
        }).ToList() ?? new List<object>();
    }

    private static List<object> FormatNamedArgs(CustomAttribute attr)
    {
        var named = new List<object>();
        if (attr.NamedArguments != null)
        {
            foreach (var na in attr.NamedArguments)
            {
                named.Add(new
                {
                    name = na.Name,
                    is_field = na.IsField,
                    type = na.Type?.FullName,
                    value = na.Value?.ToString(),
                });
            }
        }
        return named;
    }
}
