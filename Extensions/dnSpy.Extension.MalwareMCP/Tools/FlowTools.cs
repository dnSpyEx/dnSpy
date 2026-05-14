using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class FlowTools
{
    /// <summary>Build a recursive call graph from a method (depth-limited)</summary>
    [McpServerTool, Description("Build recursive call graph from a method (e.g., from Main). Shows the full execution tree to understand program flow.")]
    public static object GetCallGraph(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Starting method (Type::Method)")] string method_full_name,
        [Description("Max recursion depth (default 5)")] int max_depth = 5,
        [Description("Include external (non-assembly) calls")] bool include_external = false)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var (_, rootMethod) = MethodTools.ResolveMethod(bridge, assembly_name, method_full_name);
        var visited = new HashSet<uint>(); // prevent infinite recursion by MDToken

        object BuildNode(MethodDef method, int depth)
        {
            var token = method.MDToken.Raw;
            if (depth > max_depth || !visited.Add(token))
            {
                return new
                {
                    method = method.FullName,
                    token = $"0x{token:X8}",
                    truncated = true,
                    reason = depth > max_depth ? "max_depth_reached" : "already_visited",
                };
            }

            var callees = new List<object>();
            if (method.HasBody)
            {
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not IMethod calledRef) continue;
                    if (instr.OpCode != OpCodes.Call && instr.OpCode != OpCodes.Callvirt &&
                        instr.OpCode != OpCodes.Newobj)
                        continue;

                    // Try to resolve to a MethodDef in this module
                    var calledDef = calledRef.ResolveMethodDef();
                    if (calledDef != null && calledDef.Module == module)
                    {
                        callees.Add(BuildNode(calledDef, depth + 1));
                    }
                    else if (include_external)
                    {
                        callees.Add(new
                        {
                            method = calledRef.FullName,
                            external = true,
                            opcode = instr.OpCode.Name,
                        });
                    }
                }
            }

            return new
            {
                method = method.FullName,
                token = $"0x{token:X8}",
                declaring_type = method.DeclaringType?.FullName,
                call_count = callees.Count,
                calls = callees,
            };
        }

        return new
        {
            root = method_full_name,
            max_depth,
            graph = BuildNode(rootMethod, 0),
        };
    }

    /// <summary>Find all reads and writes to a specific field across the assembly</summary>
    [McpServerTool, Description("Find all methods that read (ldfld/ldsfld) or write (stfld/stsfld) a specific field. Critical for tracking data flow of config values, keys, flags.")]
    public static object GetFieldXrefs(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name containing the field")] string type_full_name,
        [Description("Field name")] string field_name,
        [Description("Direction: read, write, both")] string direction = "both")
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var targetType = module.GetTypes().FirstOrDefault(t => t.FullName == type_full_name)
            ?? throw new ArgumentException($"Type not found: {type_full_name}");

        var targetField = targetType.Fields.FirstOrDefault(f => f.Name?.String == field_name)
            ?? throw new ArgumentException($"Field not found: {field_name} in {type_full_name}");

        var reads = new List<object>();
        var writes = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not IField fieldRef) continue;
                    if (fieldRef.MDToken != targetField.MDToken &&
                        !(fieldRef.Name == targetField.Name &&
                          fieldRef.DeclaringType?.FullName == targetType.FullName))
                        continue;

                    bool isRead = instr.OpCode == OpCodes.Ldfld || instr.OpCode == OpCodes.Ldsfld ||
                                  instr.OpCode == OpCodes.Ldflda || instr.OpCode == OpCodes.Ldsflda;
                    bool isWrite = instr.OpCode == OpCodes.Stfld || instr.OpCode == OpCodes.Stsfld;

                    if (isRead && direction is "read" or "both")
                    {
                        reads.Add(new
                        {
                            method = method.FullName,
                            declaring_type = type.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            opcode = instr.OpCode.Name,
                        });
                    }

                    if (isWrite && direction is "write" or "both")
                    {
                        writes.Add(new
                        {
                            method = method.FullName,
                            declaring_type = type.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            opcode = instr.OpCode.Name,
                        });
                    }
                }
            }
        }

        return new
        {
            field = $"{type_full_name}::{field_name}",
            field_type = targetField.FieldType?.FullName,
            is_static = targetField.IsStatic,
            reads = direction is "read" or "both" ? reads : null,
            writes = direction is "write" or "both" ? writes : null,
            total_reads = reads.Count,
            total_writes = writes.Count,
        };
    }

    /// <summary>Get full type hierarchy: base types, derived types, interface implementations</summary>
    [McpServerTool, Description("Get type hierarchy: inheritance chain (up to System.Object), all derived types, and all interface implementations. Useful for understanding polymorphic malware.")]
    public static object GetTypeHierarchy(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name")] string type_full_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var type = module.GetTypes().FirstOrDefault(t => t.FullName == type_full_name)
            ?? throw new ArgumentException($"Type not found: {type_full_name}");

        // Walk inheritance chain upward
        var baseChain = new List<object>();
        var current = type.BaseType;
        while (current != null)
        {
            baseChain.Add(new
            {
                full_name = current.FullName,
                is_in_assembly = current.ResolveTypeDef()?.Module == module,
            });
            current = current.ResolveTypeDef()?.BaseType;
        }

        // Find all derived types in the assembly
        var derivedTypes = module.GetTypes()
            .Where(t => t.BaseType?.FullName == type.FullName)
            .Select(t => new
            {
                full_name = t.FullName,
                token = $"0x{t.MDToken.Raw:X8}",
                method_count = t.Methods?.Count ?? 0,
            })
            .ToList();

        // Interface implementations
        var interfaces = type.Interfaces?.Select(i => new
        {
            full_name = i.Interface?.FullName,
            is_in_assembly = i.Interface?.ResolveTypeDef()?.Module == module,
        }).ToList();

        // Find all types in the assembly that implement the same interfaces
        var coImplementors = new Dictionary<string, List<string>>();
        if (type.Interfaces != null)
        {
            foreach (var iface in type.Interfaces)
            {
                var ifaceName = iface.Interface?.FullName;
                if (ifaceName == null) continue;

                var implementors = module.GetTypes()
                    .Where(t => t.FullName != type.FullName &&
                                t.Interfaces?.Any(i => i.Interface?.FullName == ifaceName) == true)
                    .Select(t => t.FullName)
                    .ToList();

                if (implementors.Any())
                    coImplementors[ifaceName] = implementors;
            }
        }

        // Find virtual method overrides
        var overrides = new List<object>();
        if (type.BaseType?.ResolveTypeDef() is TypeDef baseDef)
        {
            foreach (var baseMethod in baseDef.Methods.Where(m => m.IsVirtual && !m.IsFinal))
            {
                var overrideMethod = type.Methods.FirstOrDefault(m =>
                    m.Name == baseMethod.Name && m.IsVirtual &&
                    m.MethodSig?.ToString() == baseMethod.MethodSig?.ToString());

                if (overrideMethod != null)
                {
                    overrides.Add(new
                    {
                        base_method = baseMethod.FullName,
                        override_method = overrideMethod.FullName,
                    });
                }
            }
        }

        return new
        {
            type = type.FullName,
            base_chain = baseChain,
            derived_types = derivedTypes,
            interfaces,
            co_implementors = coImplementors,
            virtual_overrides = overrides,
        };
    }
}
