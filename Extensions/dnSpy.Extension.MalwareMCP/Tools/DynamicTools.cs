using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class DynamicTools
{
    /// <summary>Find all reflection usage: Assembly.Load, Type.GetType, MethodInfo.Invoke, etc.</summary>
    [McpServerTool, Description("Find all reflection API usage: Assembly.Load/LoadFrom/LoadFile, Type.GetType, Activator.CreateInstance, MethodInfo.Invoke. Critical for understanding dynamic code loading in malware.")]
    public static object FindReflectionUsage(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var reflectionPatterns = new Dictionary<string, string[]>
        {
            ["assembly_loading"] = new[] {
                "System.Reflection.Assembly::Load",
                "System.Reflection.Assembly::LoadFrom",
                "System.Reflection.Assembly::LoadFile",
                "System.Reflection.Assembly::LoadWithPartialName",
                "System.AppDomain::Load",
            },
            ["type_resolution"] = new[] {
                "System.Type::GetType",
                "System.Reflection.Assembly::GetType",
                "System.Reflection.Assembly::GetTypes",
                "System.Reflection.Module::GetType",
            },
            ["method_invocation"] = new[] {
                "System.Reflection.MethodBase::Invoke",
                "System.Reflection.MethodInfo::Invoke",
                "System.Reflection.ConstructorInfo::Invoke",
                "System.Activator::CreateInstance",
            },
            ["member_access"] = new[] {
                "System.Type::GetMethod",
                "System.Type::GetMethods",
                "System.Type::GetField",
                "System.Type::GetFields",
                "System.Type::GetProperty",
                "System.Type::GetConstructor",
                "System.Type::GetMembers",
                "System.Type::InvokeMember",
            },
            ["dynamic_code"] = new[] {
                "System.Reflection.Emit.DynamicMethod",
                "System.Reflection.Emit.ILGenerator",
                "System.Reflection.Emit.AssemblyBuilder",
                "System.Reflection.Emit.TypeBuilder",
                "System.Reflection.Emit.MethodBuilder",
            },
            ["delegate_creation"] = new[] {
                "System.Delegate::CreateDelegate",
                "System.Runtime.InteropServices.Marshal::GetDelegateForFunctionPointer",
            },
        };

        var results = new Dictionary<string, List<object>>();
        foreach (var category in reflectionPatterns.Keys)
            results[category] = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not IMethod calledMethod) continue;
                    var calledName = calledMethod.FullName ?? "";

                    foreach (var (category, patterns) in reflectionPatterns)
                    {
                        if (patterns.Any(p => calledName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                        {
                            results[category].Add(new
                            {
                                caller_method = method.FullName,
                                declaring_type = type.FullName,
                                il_offset = $"IL_{instr.Offset:X4}",
                                called_api = calledName,
                                opcode = instr.OpCode.Name,
                            });
                        }
                    }
                }
            }
        }

        // Check for string arguments to Assembly.Load (inline payloads)
        var loadWithStringArgs = new List<object>();
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count - 1; i++)
                {
                    // ldstr followed by call Assembly.Load(string)
                    if (instrs[i].OpCode == OpCodes.Ldstr &&
                        instrs[i].Operand is string strArg &&
                        i + 1 < instrs.Count &&
                        instrs[i + 1].Operand is IMethod nextCall &&
                        (nextCall.FullName?.Contains("Assembly::Load") == true))
                    {
                        loadWithStringArgs.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instrs[i].Offset:X4}",
                            argument_preview = strArg.Length > 100 ? strArg[..100] + "..." : strArg,
                            is_base64 = strArg.Length > 40 && System.Text.RegularExpressions.Regex.IsMatch(strArg, @"^[A-Za-z0-9+/]+={0,2}$"),
                        });
                    }
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            assembly_loading = results["assembly_loading"],
            type_resolution = results["type_resolution"],
            method_invocation = results["method_invocation"],
            member_access = results["member_access"],
            dynamic_code = results["dynamic_code"],
            delegate_creation = results["delegate_creation"],
            assembly_load_with_string_args = loadWithStringArgs,
            total_reflection_calls = results.Values.Sum(v => v.Count),
        };
    }

    /// <summary>Find all delegate creation and invocation patterns</summary>
    [McpServerTool, Description("Find delegate construction (newobj Func/Action/custom) and invocation (callvirt Invoke) patterns. Malware uses delegates for indirect calls, callback-based execution, and to evade static analysis.")]
    public static object FindDelegateCreation(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var creations = new List<object>();
        var invocations = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    // Delegate creation: newobj instance void SomeDelegate::.ctor(object, native int)
                    if (instrs[i].OpCode == OpCodes.Newobj &&
                        instrs[i].Operand is IMethod ctor)
                    {
                        var ctorType = ctor.DeclaringType?.ResolveTypeDef();
                        if (ctorType?.BaseType?.FullName == "System.MulticastDelegate" ||
                            ctor.DeclaringType?.FullName?.Contains("Action") == true ||
                            ctor.DeclaringType?.FullName?.Contains("Func") == true)
                        {
                            // Try to find what method pointer is being loaded (ldftn/ldvirtftn)
                            string? targetMethod = null;
                            if (i > 0 && (instrs[i - 1].OpCode == OpCodes.Ldftn || instrs[i - 1].OpCode == OpCodes.Ldvirtftn))
                            {
                                targetMethod = (instrs[i - 1].Operand as IMethod)?.FullName;
                            }

                            creations.Add(new
                            {
                                method = method.FullName,
                                declaring_type = type.FullName,
                                il_offset = $"IL_{instrs[i].Offset:X4}",
                                delegate_type = ctor.DeclaringType?.FullName,
                                target_method = targetMethod,
                            });
                        }
                    }

                    // Delegate invocation: callvirt instance !0 Func`1::Invoke() or similar
                    if ((instrs[i].OpCode == OpCodes.Callvirt || instrs[i].OpCode == OpCodes.Call) &&
                        instrs[i].Operand is IMethod invokedMethod &&
                        invokedMethod.Name == "Invoke")
                    {
                        var declType = invokedMethod.DeclaringType;
                        var declTypeDef = declType?.ResolveTypeDef();
                        if (declTypeDef?.BaseType?.FullName == "System.MulticastDelegate" ||
                            declType?.FullName?.Contains("Action") == true ||
                            declType?.FullName?.Contains("Func") == true ||
                            declType?.FullName?.Contains("Delegate") == true)
                        {
                            invocations.Add(new
                            {
                                method = method.FullName,
                                declaring_type = type.FullName,
                                il_offset = $"IL_{instrs[i].Offset:X4}",
                                delegate_type = declType?.FullName,
                            });
                        }
                    }
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            creation_count = creations.Count,
            invocation_count = invocations.Count,
            creations,
            invocations,
        };
    }

    /// <summary>Find dynamic code generation: Reflection.Emit, DynamicMethod, CodeDom</summary>
    [McpServerTool, Description("Find dynamic code generation: DynamicMethod construction, ILGenerator usage, AssemblyBuilder/TypeBuilder, CodeDom compilation. Malware uses these to generate code at runtime to evade static analysis.")]
    public static object FindDynamicCode(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var dynamicMethods = new List<object>();
        var ilGeneratorUsage = new List<object>();
        var builderUsage = new List<object>();
        var codeDom = new List<object>();
        var marshalPatterns = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is not IMethod called) continue;
                    var calledName = called.FullName ?? "";

                    // DynamicMethod construction
                    if (calledName.Contains("DynamicMethod::.ctor"))
                    {
                        dynamicMethods.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            api = calledName,
                        });
                    }

                    // ILGenerator emit calls
                    if (calledName.Contains("ILGenerator::Emit") ||
                        calledName.Contains("ILGenerator::DeclareLocal") ||
                        calledName.Contains("ILGenerator::DefineLabel"))
                    {
                        ilGeneratorUsage.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            api = calledName,
                        });
                    }

                    // AssemblyBuilder / TypeBuilder / MethodBuilder
                    if (calledName.Contains("AssemblyBuilder") || calledName.Contains("TypeBuilder") ||
                        calledName.Contains("MethodBuilder") || calledName.Contains("ModuleBuilder"))
                    {
                        builderUsage.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            api = calledName,
                        });
                    }

                    // CodeDom / CSharpCodeProvider
                    if (calledName.Contains("CodeDom") || calledName.Contains("CSharpCodeProvider") ||
                        calledName.Contains("CompileAssemblyFromSource"))
                    {
                        codeDom.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            api = calledName,
                        });
                    }

                    // Marshal patterns for native interop (shellcode execution)
                    if (calledName.Contains("Marshal::Copy") || calledName.Contains("Marshal::AllocHGlobal") ||
                        calledName.Contains("Marshal::GetDelegateForFunctionPointer") ||
                        calledName.Contains("Marshal::PtrToStructure"))
                    {
                        marshalPatterns.Add(new
                        {
                            method = method.FullName,
                            il_offset = $"IL_{instr.Offset:X4}",
                            api = calledName,
                        });
                    }
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            dynamic_methods = dynamicMethods,
            il_generator_usage = ilGeneratorUsage,
            builder_usage = builderUsage,
            code_dom = codeDom,
            marshal_patterns = marshalPatterns,
            summary = new
            {
                has_dynamic_methods = dynamicMethods.Any(),
                has_il_generation = ilGeneratorUsage.Any(),
                has_builder_usage = builderUsage.Any(),
                has_code_dom = codeDom.Any(),
                has_marshal_patterns = marshalPatterns.Any(),
            },
        };
    }
}
