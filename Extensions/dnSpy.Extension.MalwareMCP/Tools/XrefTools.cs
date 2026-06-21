using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class XrefTools
{
    /// <summary>Find callers/callees of a method using IL scanning</summary>
    [McpServerTool, Description("Get cross-references: who calls this method, and what does it call")]
    public static object GetMethodCalls(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Full method name (Type::Method)")] string method_full_name,
        [Description("Direction: calls, callers, both")] string direction = "both")
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var (targetType, targetMethod) = MethodTools.ResolveMethod(bridge, assembly_name, method_full_name);

        var calls = new List<object>();    // what target calls
        var callers = new List<object>();  // who calls target

        // Outgoing calls from target method
        if (direction is "calls" or "both" && targetMethod.HasBody)
        {
            foreach (var instr in targetMethod.Body.Instructions)
            {
                if (instr.Operand is IMethod calledMethod &&
                    (instr.OpCode == OpCodes.Call ||
                     instr.OpCode == OpCodes.Callvirt ||
                     instr.OpCode == OpCodes.Newobj))
                {
                    calls.Add(new
                    {
                        offset = $"IL_{instr.Offset:X4}",
                        opcode = instr.OpCode.Name,
                        target = calledMethod.FullName,
                    });
                }
            }
        }

        // Incoming callers — scan all methods in module
        if (direction is "callers" or "both")
        {
            foreach (var type in module.GetTypes())
            {
                foreach (var method in type.Methods)
                {
                    if (!method.HasBody) continue;
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.Operand is IMethod called &&
                            called.MDToken == targetMethod.MDToken)
                        {
                            callers.Add(new
                            {
                                caller = method.FullName,
                                offset = $"IL_{instr.Offset:X4}",
                                opcode = instr.OpCode.Name,
                            });
                        }
                    }
                }
            }
        }

        return new
        {
            method = targetMethod.FullName,
            outgoing_calls = direction is "calls" or "both" ? calls : null,
            incoming_callers = direction is "callers" or "both" ? callers : null,
        };
    }
}
