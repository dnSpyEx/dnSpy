using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class CryptoTools
{
    /// <summary>Heuristic detection of string decryption methods</summary>
    [McpServerTool, Description("Detect likely string decryption/deobfuscation methods using heuristics. Looks for: static methods returning string, array/XOR/math operations, called from many sites. Common in ConfuserEx, .NET Reactor, custom obfuscators.")]
    public static object DetectStringEncryption(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Minimum number of call sites to qualify as decryptor")] int min_callers = 3)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        // Build a caller count map
        var callerCounts = new Dictionary<uint, int>();
        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                foreach (var instr in method.Body.Instructions)
                {
                    if (instr.Operand is IMethod called &&
                        (instr.OpCode == OpCodes.Call || instr.OpCode == OpCodes.Callvirt))
                    {
                        var def = called.ResolveMethodDef();
                        if (def != null)
                        {
                            callerCounts.TryGetValue(def.MDToken.Raw, out var count);
                            callerCounts[def.MDToken.Raw] = count + 1;
                        }
                    }
                }
            }
        }

        var candidates = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                // Heuristic 1: returns string
                bool returnsString = method.ReturnType?.FullName == "System.String";
                if (!returnsString) continue;

                // Heuristic 2: is static (most string decryptors are static)
                bool isStatic = method.IsStatic;

                // Heuristic 3: takes numeric parameters (int, uint, byte)
                bool hasNumericParams = method.Parameters
                    .Where(p => !p.IsHiddenThisParameter)
                    .Any(p =>
                    {
                        var tn = p.Type?.FullName;
                        return tn is "System.Int32" or "System.UInt32" or "System.Int64" or
                               "System.Byte" or "System.Byte[]";
                    });

                // Heuristic 4: contains crypto-like operations
                var body = method.Body;
                bool hasXor = body.Instructions.Any(i => i.OpCode == OpCodes.Xor);
                bool hasArrayAccess = body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Ldelem_U1 || i.OpCode == OpCodes.Ldelem_I1 ||
                    i.OpCode == OpCodes.Ldelem_Ref || i.OpCode == OpCodes.Stelem_I1);
                bool hasConversion = body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Conv_I1 || i.OpCode == OpCodes.Conv_U1 ||
                    i.OpCode == OpCodes.Conv_I4);
                bool hasMath = body.Instructions.Any(i =>
                    i.OpCode == OpCodes.Add || i.OpCode == OpCodes.Sub ||
                    i.OpCode == OpCodes.Mul || i.OpCode == OpCodes.Rem);

                int cryptoScore = (hasXor ? 3 : 0) + (hasArrayAccess ? 2 : 0) +
                                  (hasConversion ? 1 : 0) + (hasMath ? 1 : 0) +
                                  (isStatic ? 1 : 0) + (hasNumericParams ? 2 : 0);

                // Heuristic 5: called from multiple sites
                callerCounts.TryGetValue(method.MDToken.Raw, out var callSites);

                if (cryptoScore >= 4 || (callSites >= min_callers && returnsString && isStatic))
                {
                    candidates.Add(new
                    {
                        method_full_name = method.FullName,
                        declaring_type = type.FullName,
                        token = $"0x{method.MDToken.Raw:X8}",
                        confidence = cryptoScore >= 6 ? "high" : cryptoScore >= 4 ? "medium" : "low",
                        score = cryptoScore,
                        call_sites = callSites,
                        signals = new
                        {
                            returns_string = returnsString,
                            is_static = isStatic,
                            has_numeric_params = hasNumericParams,
                            has_xor = hasXor,
                            has_array_access = hasArrayAccess,
                            has_byte_conversion = hasConversion,
                            has_math_ops = hasMath,
                        },
                        parameter_types = method.Parameters
                            .Where(p => !p.IsHiddenThisParameter)
                            .Select(p => p.Type?.FullName).ToList(),
                        il_size = body.Instructions.Count,
                    });
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            candidates_found = candidates.Count,
            candidates = candidates.OrderByDescending(c => ((dynamic)c).score).ToList(),
        };
    }

    /// <summary>Find byte array initializations — often encryption keys, IVs, or encrypted payloads</summary>
    [McpServerTool, Description("Find all byte[] array initializations in the assembly. Detects RuntimeHelpers.InitializeArray (compile-time arrays) and inline byte assignments. These are often encryption keys, IVs, XOR tables, or encrypted payloads.")]
    public static object FindByteArrays(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Minimum array size in bytes")] int min_size = 8,
        [Description("Max results")] int limit = 100)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var results = new List<object>();

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || results.Count >= limit) continue;

                var instrs = method.Body.Instructions;
                for (int i = 0; i < instrs.Count; i++)
                {
                    // Pattern 1: newarr byte followed by RuntimeHelpers.InitializeArray
                    // This is how the C# compiler handles `new byte[] { 0x01, 0x02, ... }`
                    if (instrs[i].OpCode == OpCodes.Newarr &&
                        instrs[i].Operand is ITypeDefOrRef arrType &&
                        arrType.FullName == "System.Byte")
                    {
                        // Look for size (ldc.i4 before newarr)
                        int size = 0;
                        if (i > 0)
                            size = GetIntValue(instrs[i - 1]);

                        if (size < min_size) continue;

                        // Look for InitializeArray call after newarr
                        byte[]? arrayData = null;
                        if (i + 2 < instrs.Count &&
                            instrs[i + 1].OpCode == OpCodes.Dup &&
                            instrs[i + 2].OpCode == OpCodes.Ldtoken &&
                            instrs[i + 2].Operand is FieldDef fieldToken)
                        {
                            // The field's initial value contains the raw bytes
                            arrayData = fieldToken.InitialValue;
                        }
                        else if (i + 1 < instrs.Count &&
                                 instrs[i + 1].OpCode == OpCodes.Ldtoken &&
                                 instrs[i + 1].Operand is FieldDef fieldToken2)
                        {
                            arrayData = fieldToken2.InitialValue;
                        }

                        var entry = new
                        {
                            method_full_name = method.FullName,
                            declaring_type = type.FullName,
                            il_offset = $"IL_{instrs[i].Offset:X4}",
                            array_size = size,
                            has_data = arrayData != null,
                            entropy = arrayData != null ? Math.Round(Utils.Entropy.Calculate(arrayData), 2) : 0.0,
                            data_preview = arrayData != null
                                ? BitConverter.ToString(arrayData, 0, Math.Min(32, arrayData.Length)).Replace("-", " ")
                                : null,
                            data_base64 = arrayData != null && arrayData.Length <= 4096
                                ? Convert.ToBase64String(arrayData)
                                : null,
                            is_high_entropy = arrayData != null && Utils.Entropy.Calculate(arrayData) > 7.0,
                        };

                        results.Add(entry);
                    }

                    // Pattern 2: static byte[] fields with initial values
                    if (instrs[i].OpCode == OpCodes.Ldsfld &&
                        instrs[i].Operand is FieldDef staticField &&
                        staticField.FieldType?.FullName == "System.Byte[]" &&
                        staticField.InitialValue != null &&
                        staticField.InitialValue.Length >= min_size)
                    {
                        // Avoid duplicates
                        if (results.Any(r => ((dynamic)r).method_full_name == method.FullName &&
                                             ((dynamic)r).il_offset == $"IL_{instrs[i].Offset:X4}"))
                            continue;

                        var data = staticField.InitialValue;
                        results.Add(new
                        {
                            method_full_name = method.FullName,
                            declaring_type = type.FullName,
                            il_offset = $"IL_{instrs[i].Offset:X4}",
                            field_name = staticField.FullName,
                            array_size = data.Length,
                            has_data = true,
                            entropy = Math.Round(Utils.Entropy.Calculate(data), 2),
                            data_preview = BitConverter.ToString(data, 0, Math.Min(32, data.Length)).Replace("-", " "),
                            data_base64 = data.Length <= 4096 ? Convert.ToBase64String(data) : null,
                            is_high_entropy = Utils.Entropy.Calculate(data) > 7.0,
                        });
                    }
                }
            }
        }

        // Also scan all fields with InitialValue directly
        foreach (var type in module.GetTypes())
        {
            foreach (var field in type.Fields)
            {
                if (results.Count >= limit) break;
                if (field.InitialValue == null || field.InitialValue.Length < min_size) continue;
                if (field.FieldType?.FullName != "System.Byte[]") continue;

                // Skip if already found via method scanning
                if (results.Any(r => ((dynamic)r).has_data == (object)true &&
                                     ((string?)((dynamic)r).field_name)?.Contains(field.Name?.String ?? "___") == true))
                    continue;

                var data = field.InitialValue;
                results.Add(new
                {
                    method_full_name = (string?)null,
                    declaring_type = type.FullName,
                    il_offset = (string?)null,
                    field_name = field.FullName,
                    array_size = data.Length,
                    has_data = true,
                    entropy = Math.Round(Utils.Entropy.Calculate(data), 2),
                    data_preview = BitConverter.ToString(data, 0, Math.Min(32, data.Length)).Replace("-", " "),
                    data_base64 = data.Length <= 4096 ? Convert.ToBase64String(data) : null,
                    is_high_entropy = Utils.Entropy.Calculate(data) > 7.0,
                });
            }
        }

        return new
        {
            assembly = assembly_name,
            total = results.Count,
            arrays = results,
        };
    }

    /// <summary>Detect PE files (MZ header) embedded in resources or byte arrays</summary>
    [McpServerTool, Description("Detect PE files (executables/DLLs) embedded in resources or byte arrays. Common in .NET loaders, droppers, and packed malware that carry second-stage payloads.")]
    public static object FindEmbeddedPEs(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var results = new List<object>();

        // Scan embedded resources
        if (module.Resources != null)
        {
            foreach (var r in module.Resources)
            {
                if (r is not EmbeddedResource er) continue;

                var data = er.CreateReader().ToArray();
                ScanForPE(data, $"resource:{r.Name?.String}", r.Name?.String, results);
            }
        }

        // Scan byte[] fields with InitialValue
        foreach (var type in module.GetTypes())
        {
            foreach (var field in type.Fields)
            {
                if (field.InitialValue == null || field.InitialValue.Length < 64) continue;
                ScanForPE(field.InitialValue, $"field:{field.FullName}", field.FullName, results);
            }
        }

        return new
        {
            assembly = assembly_name,
            embedded_pe_count = results.Count,
            embedded_pes = results,
        };
    }

    private static void ScanForPE(byte[] data, string location, string? name, List<object> results)
    {
        // Check for MZ header at start
        if (data.Length >= 64 && data[0] == 0x4D && data[1] == 0x5A)
        {
            var peInfo = AnalyzePEHeader(data);
            results.Add(new
            {
                location,
                name,
                size = data.Length,
                entropy = Math.Round(Utils.Entropy.Calculate(data), 2),
                pe_info = peInfo,
            });
            return;
        }

        // Scan for MZ header at other offsets (sometimes padded)
        for (int i = 0; i < Math.Min(data.Length - 64, 1024); i++)
        {
            if (data[i] == 0x4D && data[i + 1] == 0x5A)
            {
                // Verify PE signature at e_lfanew offset
                if (i + 0x3C + 4 < data.Length)
                {
                    int peOffset = BitConverter.ToInt32(data, i + 0x3C);
                    if (peOffset > 0 && i + peOffset + 4 < data.Length &&
                        data[i + peOffset] == 0x50 && data[i + peOffset + 1] == 0x45 &&
                        data[i + peOffset + 2] == 0x00 && data[i + peOffset + 3] == 0x00)
                    {
                        var subData = new byte[data.Length - i];
                        Array.Copy(data, i, subData, 0, subData.Length);
                        var peInfo = AnalyzePEHeader(subData);
                        results.Add(new
                        {
                            location = $"{location} (offset 0x{i:X})",
                            name,
                            offset_in_container = i,
                            size = data.Length - i,
                            entropy = Math.Round(Utils.Entropy.Calculate(subData), 2),
                            pe_info = peInfo,
                        });
                        return;
                    }
                }
            }
        }
    }

    private static object AnalyzePEHeader(byte[] data)
    {
        try
        {
            int peOffset = BitConverter.ToInt32(data, 0x3C);
            bool validPE = peOffset > 0 && peOffset + 6 < data.Length &&
                           data[peOffset] == 0x50 && data[peOffset + 1] == 0x45;

            if (!validPE) return new { valid = false };

            ushort machine = BitConverter.ToUInt16(data, peOffset + 4);
            ushort sections = BitConverter.ToUInt16(data, peOffset + 6);

            // Check for .NET CLR header
            bool isDotNet = false;
            if (peOffset + 0x98 + 8 < data.Length)
            {
                int clrRva = BitConverter.ToInt32(data, peOffset + 0xE8); // COM descriptor in PE32+
                if (clrRva == 0 && peOffset + 0xD8 + 4 < data.Length)
                    clrRva = BitConverter.ToInt32(data, peOffset + 0xD8); // PE32
                isDotNet = clrRva > 0;
            }

            return new
            {
                valid = true,
                machine = machine switch
                {
                    0x14C => "x86",
                    0x8664 => "x64",
                    0x1C0 => "ARM",
                    0xAA64 => "ARM64",
                    _ => $"0x{machine:X4}",
                },
                sections,
                is_dotnet = isDotNet,
            };
        }
        catch
        {
            return new { valid = false, error = "Failed to parse PE header" };
        }
    }

    private static int GetIntValue(Instruction instr)
    {
        if (instr.OpCode == OpCodes.Ldc_I4) return (int)instr.Operand;
        if (instr.OpCode == OpCodes.Ldc_I4_S) return (sbyte)instr.Operand;
        if (instr.OpCode == OpCodes.Ldc_I4_0) return 0;
        if (instr.OpCode == OpCodes.Ldc_I4_1) return 1;
        if (instr.OpCode == OpCodes.Ldc_I4_2) return 2;
        if (instr.OpCode == OpCodes.Ldc_I4_3) return 3;
        if (instr.OpCode == OpCodes.Ldc_I4_4) return 4;
        if (instr.OpCode == OpCodes.Ldc_I4_5) return 5;
        if (instr.OpCode == OpCodes.Ldc_I4_6) return 6;
        if (instr.OpCode == OpCodes.Ldc_I4_7) return 7;
        if (instr.OpCode == OpCodes.Ldc_I4_8) return 8;
        if (instr.OpCode == OpCodes.Ldc_I4_M1) return -1;
        return 0;
    }
}
