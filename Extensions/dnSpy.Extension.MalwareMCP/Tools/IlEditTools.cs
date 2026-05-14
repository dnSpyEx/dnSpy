using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

/// <summary>
/// IL-level editing tools: patch method bodies, neuter checks, replace return values.
/// All mutations are in-memory (visible immediately in dnSpy). Use SaveTools.SaveAssembly to persist.
/// </summary>
[McpServerToolType]
public static class IlEditTools
{
    [McpServerTool, Description("Replace a method body so it returns immediately. For void methods just emits 'ret'. For value-returning methods emits a default value (0/null) then 'ret'. Useful to neuter anti-debug/license checks. Requires confirm=true.")]
    public static object ReplaceWithReturnDefault(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (Type::Method)")] string method_full_name,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!MethodTools.TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err) || method == null)
            return new { error = err };
        var m = method;
        if (!m.HasBody)
            return new { error = "Method has no body (abstract/extern/pinvoke)" };
        if (!confirm)
            return new { status = "dry_run", message = $"Would replace body of '{m.FullName}' with return-default. Set confirm=true to execute." };

        bridge.RunOnUiThread(() =>
        {
            var body = m.Body;
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.Instructions.Clear();
            EmitDefaultForType(body.Instructions, m.ReturnType, m.Module);
            body.Instructions.Add(OpCodes.Ret.ToInstruction());
            body.UpdateInstructionOffsets();
        });
        return new { status = "patched", method = m.FullName, body_size = m.Body.Instructions.Count };
    }

    [McpServerTool, Description("Replace a method body with a constant boolean return (true/false). Method must return bool. Requires confirm=true.")]
    public static object ReplaceWithBoolReturn(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (Type::Method)")] string method_full_name,
        [Description("Value to return")] bool value,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!MethodTools.TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err) || method == null)
            return new { error = err };
        var m = method;
        if (!m.HasBody)
            return new { error = "Method has no body" };
        var rt = m.ReturnType?.FullName;
        if (rt != "System.Boolean")
            return new { error = $"Method must return bool, but returns {rt}" };
        if (!confirm)
            return new { status = "dry_run", message = $"Would patch '{m.FullName}' to return {value}." };

        bridge.RunOnUiThread(() =>
        {
            var body = m.Body;
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.Instructions.Clear();
            body.Instructions.Add(OpCodes.Ldc_I4.ToInstruction(value ? 1 : 0));
            body.Instructions.Add(OpCodes.Ret.ToInstruction());
            body.UpdateInstructionOffsets();
        });
        return new { status = "patched", method = m.FullName, returns = value };
    }

    [McpServerTool, Description("Replace a method body with 'throw new NotImplementedException()'. Requires confirm=true.")]
    public static object ReplaceWithThrow(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (Type::Method)")] string method_full_name,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!MethodTools.TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err) || method == null)
            return new { error = err };
        var m = method;
        if (!m.HasBody)
            return new { error = "Method has no body" };
        if (!confirm)
            return new { status = "dry_run", message = $"Would replace body of '{m.FullName}' with throw NotImplementedException." };

        bridge.RunOnUiThread(() =>
        {
            var module = m.Module;
            var nieType = module.CorLibTypes.GetTypeRef("System", "NotImplementedException");
            var ctor = new MemberRefUser(module, ".ctor",
                MethodSig.CreateInstance(module.CorLibTypes.Void), nieType);
            var body = m.Body;
            body.ExceptionHandlers.Clear();
            body.Variables.Clear();
            body.Instructions.Clear();
            body.Instructions.Add(OpCodes.Newobj.ToInstruction(ctor));
            body.Instructions.Add(OpCodes.Throw.ToInstruction());
            body.UpdateInstructionOffsets();
        });
        return new { status = "patched", method = m.FullName };
    }

    [McpServerTool, Description("NOP-out an instruction at the given IL offset (e.g., 'IL_0042' or '0x42' or '66'). Useful to disable a branch/call without changing layout. Requires confirm=true.")]
    public static object NopInstruction(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name")] string method_full_name,
        [Description("IL offset, e.g. 'IL_0042' / '0x42' / '66'")] string il_offset,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!MethodTools.TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err) || method == null)
            return new { error = err };
        var m = method;
        if (!m.HasBody) return new { error = "Method has no body" };

        if (!TryParseOffset(il_offset, out var offset))
            return new { error = $"Bad offset format: {il_offset}" };

        var instr = m.Body.Instructions.FirstOrDefault(i => i.Offset == offset);
        if (instr == null)
            return new { error = $"No instruction at offset 0x{offset:X4}" };

        var orig = $"{instr.OpCode.Name} {MethodTools.FormatOperand(instr)}".Trim();
        if (!confirm)
            return new { status = "dry_run", message = $"Would NOP-out '{orig}' at IL_{offset:X4}." };

        bridge.RunOnUiThread(() =>
        {
            instr.OpCode = OpCodes.Nop;
            instr.Operand = null;
            m.Body.UpdateInstructionOffsets();
        });
        return new { status = "patched", offset = $"IL_{offset:X4}", original = orig };
    }

    [McpServerTool, Description("Replace one instruction with another simple opcode (no operand). Examples: 'nop','ret','ldnull','ldc.i4.0','ldc.i4.1','pop','dup','throw','endfinally'. Requires confirm=true.")]
    public static object ReplaceOpcode(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name")] string method_full_name,
        [Description("IL offset")] string il_offset,
        [Description("New opcode mnemonic")] string opcode,
        [Description("Must be true to execute")] bool confirm = false)
    {
        if (!MethodTools.TryResolveMethod(bridge, assembly_name, method_full_name, out _, out var method, out var err) || method == null)
            return new { error = err };
        var m = method;
        if (!m.HasBody) return new { error = "Method has no body" };
        if (!TryParseOffset(il_offset, out var offset))
            return new { error = $"Bad offset format: {il_offset}" };
        if (!TryGetSimpleOpcode(opcode, out var newOp))
            return new { error = $"Opcode not supported by this tool: {opcode}. Use one of nop/ret/ldnull/ldc.i4.0/ldc.i4.1/pop/dup/throw/endfinally." };

        var instr = m.Body.Instructions.FirstOrDefault(i => i.Offset == offset);
        if (instr == null) return new { error = $"No instruction at IL_{offset:X4}" };

        var orig = $"{instr.OpCode.Name} {MethodTools.FormatOperand(instr)}".Trim();
        if (!confirm)
            return new { status = "dry_run", message = $"Would replace '{orig}' with '{newOp.Name}' at IL_{offset:X4}." };

        bridge.RunOnUiThread(() =>
        {
            instr.OpCode = newOp;
            instr.Operand = null;
            m.Body.UpdateInstructionOffsets();
        });
        return new { status = "patched", offset = $"IL_{offset:X4}", original = orig, replaced_with = newOp.Name };
    }

    [McpServerTool, Description("Remove all custom attributes whose type-name contains the given substring (case-insensitive) from a type or method. Useful for stripping obfuscator markers. Requires confirm=true.")]
    public static object StripAttributes(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full type name OR Type::Method to target")] string target,
        [Description("Substring to match in attribute type-name (e.g., 'Confused', 'Obfusc', 'SmartAssembly')")] string contains,
        [Description("Must be true to execute")] bool confirm = false)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        IHasCustomAttribute targetProvider;
        string targetName;

        if (target.Contains("::"))
        {
            if (!MethodTools.TryResolveMethod(bridge, assembly_name, target, out _, out var method, out var err) || method == null)
                return new { error = err };
            targetProvider = method;
            targetName = method.FullName;
        }
        else
        {
            var type = module.GetTypes().FirstOrDefault(t => t.FullName == target);
            if (type == null) return new { error = $"Type not found: {target}" };
            targetProvider = type;
            targetName = type.FullName;
        }

        var matches = targetProvider.CustomAttributes
            .Where(a => (a.AttributeType?.FullName ?? "").IndexOf(contains, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        if (matches.Count == 0)
            return new { status = "noop", message = $"No matching attributes on {targetName}." };

        if (!confirm)
            return new
            {
                status = "dry_run",
                count = matches.Count,
                attributes = matches.Select(a => a.AttributeType?.FullName).ToList(),
                target = targetName,
            };

        bridge.RunOnUiThread(() =>
        {
            foreach (var a in matches)
                targetProvider.CustomAttributes.Remove(a);
        });
        return new { status = "stripped", removed = matches.Count, target = targetName };
    }

    static void EmitDefaultForType(IList<Instruction> il, TypeSig? sig, ModuleDef module)
    {
        if (sig == null || sig.ElementType == ElementType.Void)
            return;

        switch (sig.ElementType)
        {
            case ElementType.Boolean:
            case ElementType.Char:
            case ElementType.I1:
            case ElementType.U1:
            case ElementType.I2:
            case ElementType.U2:
            case ElementType.I4:
            case ElementType.U4:
                il.Add(OpCodes.Ldc_I4_0.ToInstruction());
                break;
            case ElementType.I8:
            case ElementType.U8:
                il.Add(OpCodes.Ldc_I4_0.ToInstruction());
                il.Add(OpCodes.Conv_I8.ToInstruction());
                break;
            case ElementType.R4:
                il.Add(OpCodes.Ldc_R4.ToInstruction(0f));
                break;
            case ElementType.R8:
                il.Add(OpCodes.Ldc_R8.ToInstruction(0d));
                break;
            case ElementType.String:
            case ElementType.Class:
            case ElementType.Object:
            case ElementType.SZArray:
            case ElementType.Array:
                il.Add(OpCodes.Ldnull.ToInstruction());
                break;
            case ElementType.ValueType:
            case ElementType.GenericInst:
            case ElementType.Var:
            case ElementType.MVar:
                throw new NotSupportedException("Returning a value-type/generic default isn't supported by this simple emitter; use a more specific tool or rewrite manually.");
            default:
                il.Add(OpCodes.Ldnull.ToInstruction());
                break;
        }
    }

    static bool TryParseOffset(string s, out uint offset)
    {
        offset = 0;
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        if (s.StartsWith("IL_", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.Substring(3), System.Globalization.NumberStyles.HexNumber, null, out offset);
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return uint.TryParse(s.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out offset);
        return uint.TryParse(s, out offset);
    }

    static bool TryGetSimpleOpcode(string mnem, out OpCode op)
    {
        switch (mnem.Trim().ToLowerInvariant())
        {
            case "nop": op = OpCodes.Nop; return true;
            case "ret": op = OpCodes.Ret; return true;
            case "ldnull": op = OpCodes.Ldnull; return true;
            case "ldc.i4.0": op = OpCodes.Ldc_I4_0; return true;
            case "ldc.i4.1": op = OpCodes.Ldc_I4_1; return true;
            case "pop": op = OpCodes.Pop; return true;
            case "dup": op = OpCodes.Dup; return true;
            case "throw": op = OpCodes.Throw; return true;
            case "endfinally": op = OpCodes.Endfinally; return true;
            default: op = OpCodes.Nop; return false;
        }
    }
}
