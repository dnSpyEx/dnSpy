using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class MethodTools
{
    /// <summary>Decompile a type to C# using dnSpy's ILSpy engine</summary>
    [McpServerTool, Description("Decompile a type to C# source code using dnSpy's built-in decompiler")]
    public static object DecompileType(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name")] string type_full_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var type = module.GetTypes()
            .FirstOrDefault(t => t.FullName == type_full_name)
            ?? throw new ArgumentException($"Type not found: {type_full_name}");

        var source = bridge.DecompileType(type);
        return new { type_full_name, language = "C#", source_code = source };
    }

    /// <summary>Decompile a specific method to C#</summary>
    [McpServerTool, Description("Decompile a method to C# source code")]
    public static object DecompileMethod(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (e.g., 'Stealer.Config::Decrypt')")] string method_full_name)
    {
        if (!TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err))
            return new { error = err, method_full_name };
        var source = bridge.DecompileMethod(method!);
        return new { method_full_name = method!.FullName, language = "C#", source_code = source };
    }

    /// <summary>Get CIL/IL disassembly of a method</summary>
    [McpServerTool, Description("Get raw CIL/IL instructions for a method")]
    public static object DisassembleMethod(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name")] string method_full_name)
    {
        if (!TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var methodOpt, out var err))
            return new { error = err, method_full_name };
        var method = methodOpt!;

        if (!method.HasBody)
            return new { error = "Method has no body (abstract/extern/pinvoke)" };

        var body = method.Body;
        var instructions = body.Instructions.Select(i => new
        {
            offset = $"IL_{i.Offset:X4}",
            opcode = i.OpCode.Name,
            operand = FormatOperand(i),
        }).ToList();

        var locals = body.Variables?.Select(v => new
        {
            index = v.Index,
            type_name = v.Type?.FullName,
        }).ToList();

        var exception_handlers = body.ExceptionHandlers?.Select(eh => new
        {
            handler_type = eh.HandlerType.ToString(),
            try_start = $"IL_{eh.TryStart?.Offset:X4}",
            try_end = $"IL_{eh.TryEnd?.Offset:X4}",
            catch_type = eh.CatchType?.FullName,
        }).ToList();

        return new
        {
            method_full_name = method.FullName,
            code_size = body.Instructions.Count,
            max_stack = body.MaxStack,
            init_locals = body.InitLocals,
            locals,
            instructions,
            exception_handlers,
        };
    }

    /// <summary>Resolve "TypeName::MethodName" to (TypeDef, MethodDef)</summary>
    internal static (TypeDef, MethodDef) ResolveMethod(DnSpyBridge bridge, string asmName, string fullName)
    {
        if (!TryResolveMethod(bridge, asmName, fullName, out var type, out var method, out var err))
            throw new ArgumentException(err);
        return (type!, method!);
    }

    /// <summary>Non-throwing resolver — returns false + error message when the target can't be found.</summary>
    internal static bool TryResolveMethod(
        DnSpyBridge bridge, string asmName, string fullName,
        out TypeDef? type, out MethodDef? method, out string? error)
    {
        type = null;
        method = null;
        error = null;

        var module = bridge.FindModule(asmName);
        if (module == null) { error = $"Assembly not found: {asmName}"; return false; }

        var parts = fullName.Split("::", 2);
        if (parts.Length == 2)
        {
            type = module.GetTypes().FirstOrDefault(t => t.FullName == parts[0]);
            if (type == null) { error = $"Type not found: {parts[0]}"; return false; }
            method = type.Methods.FirstOrDefault(m => m.Name == parts[1] || m.FullName == fullName);
            if (method == null) { error = $"Method not found: {parts[1]} in {parts[0]}"; return false; }
            return true;
        }

        foreach (var t in module.GetTypes())
            foreach (var m in t.Methods)
                if (m.FullName == fullName || m.Name == fullName)
                {
                    type = t;
                    method = m;
                    return true;
                }

        error = $"Method not found: {fullName}";
        return false;
    }

    internal static string FormatOperand(Instruction instr)
    {
        if (instr.Operand == null) return "";
        return instr.Operand switch
        {
            IMethod m => m.FullName,
            IField f => f.FullName,
            ITypeDefOrRef t => t.FullName,
            string s => $"\"{s}\"",
            Instruction target => $"IL_{target.Offset:X4}",
            _ => instr.Operand.ToString() ?? "",
        };
    }
}
