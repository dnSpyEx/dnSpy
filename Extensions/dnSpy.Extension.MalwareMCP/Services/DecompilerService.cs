using dnSpy.Contracts.Decompiler;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP;

/// <summary>
/// Wrapper around dnSpy's decompiler for convenient decompilation operations.
/// Provides language selection and output formatting.
/// </summary>
sealed class DecompilerHelper
{
    private readonly DnSpyBridge _bridge;

    public DecompilerHelper(DnSpyBridge bridge)
    {
        _bridge = bridge;
    }

    /// <summary>Decompile a type and return C# source</summary>
    public string DecompileType(TypeDef typeDef) => _bridge.DecompileType(typeDef);

    /// <summary>Decompile a method and return C# source</summary>
    public string DecompileMethod(MethodDef methodDef) => _bridge.DecompileMethod(methodDef);

    /// <summary>Get IL disassembly of a method body</summary>
    public string GetILDisassembly(MethodDef methodDef)
    {
        if (!methodDef.HasBody)
            return "// Method has no body (abstract/extern/pinvoke)";

        var sb = new System.Text.StringBuilder();
        var body = methodDef.Body;

        sb.AppendLine($"// Code size: {body.Instructions.Count} instructions");
        sb.AppendLine($"// MaxStack: {body.MaxStack}");
        if (body.InitLocals)
            sb.AppendLine("// .locals init");

        if (body.Variables?.Count > 0)
        {
            sb.AppendLine(".locals (");
            foreach (var v in body.Variables)
                sb.AppendLine($"    [{v.Index}] {v.Type?.FullName}");
            sb.AppendLine(")");
        }

        sb.AppendLine();
        foreach (var instr in body.Instructions)
        {
            sb.AppendLine($"IL_{instr.Offset:X4}: {instr.OpCode.Name} {FormatOperand(instr)}");
        }

        return sb.ToString();
    }

    private static string FormatOperand(dnlib.DotNet.Emit.Instruction instr)
    {
        if (instr.Operand == null) return "";
        return instr.Operand switch
        {
            dnlib.DotNet.IMethod m => m.FullName,
            dnlib.DotNet.IField f => f.FullName,
            dnlib.DotNet.ITypeDefOrRef t => t.FullName,
            string s => $"\"{s}\"",
            dnlib.DotNet.Emit.Instruction target => $"IL_{target.Offset:X4}",
            _ => instr.Operand.ToString() ?? "",
        };
    }
}
