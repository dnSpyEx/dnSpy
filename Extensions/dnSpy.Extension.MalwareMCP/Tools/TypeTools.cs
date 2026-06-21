using ModelContextProtocol.Server;
using System.ComponentModel;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class TypeTools
{
    /// <summary>List types (classes, structs, interfaces, enums) with filtering</summary>
    [McpServerTool, Description("List types in assembly with optional filtering by namespace, name, or kind")]
    public static object ListTypes(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Namespace substring filter")] string? filter_namespace = null,
        [Description("Type name substring filter")] string? filter_name = null,
        [Description("Kind filter: class, interface, struct, enum, delegate, all")] string filter_kind = "all",
        [Description("Include compiler-generated types")] bool include_generated = false,
        [Description("Max results (default 100)")] int limit = 100,
        [Description("Pagination offset")] int offset = 0)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var types = module.GetTypes().AsEnumerable();

        if (!include_generated)
            types = types.Where(t => !t.Name.String.Contains("<") && !t.Name.String.Contains(">"));

        if (!string.IsNullOrEmpty(filter_namespace))
            types = types.Where(t => (t.Namespace?.String ?? "").Contains(filter_namespace, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(filter_name))
            types = types.Where(t => t.Name.String.Contains(filter_name, StringComparison.OrdinalIgnoreCase));

        if (filter_kind != "all")
            types = filter_kind switch
            {
                "interface" => types.Where(t => t.IsInterface),
                "enum" => types.Where(t => t.IsEnum),
                "struct" => types.Where(t => t.IsValueType && !t.IsEnum),
                "delegate" => types.Where(t => t.BaseType?.FullName == "System.MulticastDelegate"),
                "class" => types.Where(t => t.IsClass && !t.IsDelegate),
                _ => types,
            };

        var list = types.ToList();
        var paged = list.Skip(offset).Take(limit);

        return new
        {
            total = list.Count,
            offset,
            limit,
            types = paged.Select(t => new
            {
                token = $"0x{t.MDToken.Raw:X8}",
                name = t.Name?.String,
                @namespace = t.Namespace?.String,
                full_name = t.FullName,
                kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" :
                       t.IsValueType ? "struct" : t.IsDelegate ? "delegate" : "class",
                visibility = t.Visibility.ToString(),
                base_type = t.BaseType?.FullName,
                interface_count = t.Interfaces?.Count ?? 0,
                method_count = t.Methods?.Count ?? 0,
                field_count = t.Fields?.Count ?? 0,
                has_cctor = t.FindStaticConstructor() != null,
            }).ToList(),
        };
    }

    /// <summary>Get full details of a specific type</summary>
    [McpServerTool, Description("Get detailed type info: all fields, methods, properties, events")]
    public static object GetTypeDetails(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name (e.g., 'Stealer.Config')")] string type_full_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var type = module.GetTypes()
            .FirstOrDefault(t => t.FullName == type_full_name)
            ?? throw new ArgumentException($"Type not found: {type_full_name}");

        return new
        {
            token = $"0x{type.MDToken.Raw:X8}",
            full_name = type.FullName,
            kind = type.IsInterface ? "interface" : type.IsEnum ? "enum" :
                   type.IsValueType ? "struct" : "class",
            base_type = type.BaseType?.FullName,
            interfaces = type.Interfaces?.Select(i => i.Interface?.FullName).ToList(),
            custom_attributes = type.CustomAttributes?.Select(a => a.TypeFullName).ToList(),

            fields = type.Fields?.Select(f => new
            {
                token = $"0x{f.MDToken.Raw:X8}",
                name = f.Name?.String,
                type_name = f.FieldType?.FullName,
                is_static = f.IsStatic,
                is_literal = f.IsLiteral,
                is_init_only = f.IsInitOnly,
                visibility = f.Access.ToString(),
                constant = f.Constant?.Value?.ToString(),
            }).ToList(),

            methods = type.Methods?.Select(m => new
            {
                token = $"0x{m.MDToken.Raw:X8}",
                name = m.Name?.String,
                full_name = m.FullName,
                return_type = m.ReturnType?.FullName,
                parameters = m.Parameters?
                    .Where(p => !p.IsHiddenThisParameter)
                    .Select(p => new { name = p.Name, type_name = p.Type?.FullName })
                    .ToList(),
                is_static = m.IsStatic,
                is_virtual = m.IsVirtual,
                is_constructor = m.IsConstructor,
                visibility = m.Access.ToString(),
                il_code_size = m.Body?.Instructions?.Count ?? 0,
                has_body = m.HasBody,
            }).ToList(),

            properties = type.Properties?.Select(p => new
            {
                name = p.Name?.String,
                type_name = p.PropertySig?.RetType?.FullName,
                has_getter = p.GetMethod != null,
                has_setter = p.SetMethod != null,
            }).ToList(),

            events = type.Events?.Select(e => new
            {
                name = e.Name?.String,
                type_name = e.EventType?.FullName,
            }).ToList(),
        };
    }
}
