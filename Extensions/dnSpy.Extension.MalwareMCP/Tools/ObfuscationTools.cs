using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.RegularExpressions;
using dnlib.DotNet;
using dnlib.DotNet.Emit;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class ObfuscationTools
{
    /// <summary>Detect obfuscator by watermarks, attributes, and structural signatures</summary>
    [McpServerTool, Description("Identify the obfuscator used on an assembly by checking watermarks, custom attributes, type/resource naming patterns, and structural signatures. Detects: ConfuserEx, .NET Reactor, SmartAssembly, Dotfuscator, Babel, Eazfuscator, Crypto Obfuscator, Agile.NET, Spices.Net, Xenocode, and others.")]
    public static object DetectObfuscator(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var detections = new List<object>();

        // Gather all attribute names
        var allAttributes = new HashSet<string>();
        if (module.Assembly?.CustomAttributes != null)
            foreach (var attr in module.Assembly.CustomAttributes)
                allAttributes.Add(attr.TypeFullName ?? "");
        foreach (var type in module.GetTypes())
        {
            if (type.CustomAttributes != null)
                foreach (var attr in type.CustomAttributes)
                    allAttributes.Add(attr.TypeFullName ?? "");
        }

        var allTypeNames = module.GetTypes().Select(t => t.FullName ?? "").ToList();
        var allResourceNames = module.Resources?.Select(r => r.Name?.String ?? "").ToList() ?? new List<string>();

        // ConfuserEx
        if (allAttributes.Any(a => a.Contains("ConfusedBy")) ||
            allTypeNames.Any(n => n.Contains("ConfuserEx")) ||
            module.Assembly?.CustomAttributes?.Any(a =>
                a.ConstructorArguments.Any(arg => arg.Value?.ToString()?.Contains("ConfuserEx") == true)) == true)
        {
            detections.Add(new { obfuscator = "ConfuserEx", confidence = "high", evidence = "ConfuserEx attribute watermark" });
        }

        // .NET Reactor
        if (allTypeNames.Any(n => Regex.IsMatch(n, @"^__\$")) ||
            allResourceNames.Any(n => n.Contains("__") && n.EndsWith(".resources")) ||
            allAttributes.Any(a => a.Contains("NETReactor") || a.Contains("ReactorAttribute")))
        {
            detections.Add(new { obfuscator = ".NET Reactor", confidence = "high", evidence = "Reactor naming patterns or attributes" });
        }

        // SmartAssembly
        if (allAttributes.Any(a => a.Contains("SmartAssembly") || a.Contains("PoweredBy")) ||
            allTypeNames.Any(n => n.Contains("SmartAssembly")) ||
            allResourceNames.Any(n => n.Contains("{") && n.Contains("}") && n.Length > 30))
        {
            detections.Add(new { obfuscator = "SmartAssembly", confidence = "medium", evidence = "SmartAssembly markers" });
        }

        // Dotfuscator
        if (allAttributes.Any(a => a.Contains("Dotfuscator") || a.Contains("DotfuscatorAttribute")))
        {
            detections.Add(new { obfuscator = "Dotfuscator", confidence = "high", evidence = "Dotfuscator attribute" });
        }

        // Babel Obfuscator
        if (allAttributes.Any(a => a.Contains("Babel") || a.Contains("babelAttribute")) ||
            allTypeNames.Any(n => Regex.IsMatch(n, @"^\u0001") || n.Contains("Babel")))
        {
            detections.Add(new { obfuscator = "Babel Obfuscator", confidence = "medium", evidence = "Babel markers" });
        }

        // Eazfuscator.NET
        if (allAttributes.Any(a => a.Contains("Eazfuscator") || a.Contains("EazAttribute")) ||
            allTypeNames.Any(n => n.Contains("Eaz")))
        {
            detections.Add(new { obfuscator = "Eazfuscator.NET", confidence = "medium", evidence = "Eazfuscator markers" });
        }

        // Crypto Obfuscator
        if (allAttributes.Any(a => a.Contains("CryptoObfuscator") || a.Contains("CoAttribute")))
        {
            detections.Add(new { obfuscator = "Crypto Obfuscator", confidence = "high", evidence = "CryptoObfuscator attribute" });
        }

        // Agile.NET (CliSecure)
        if (allAttributes.Any(a => a.Contains("Agile") || a.Contains("CliSecure")) ||
            allResourceNames.Any(n => n == "AgileDotNetRT" || n == "AgileDotNetRT64"))
        {
            detections.Add(new { obfuscator = "Agile.NET (CliSecure)", confidence = "high", evidence = "Agile.NET runtime resource" });
        }

        // Obfuscar
        if (allAttributes.Any(a => a.Contains("Obfuscar")))
        {
            detections.Add(new { obfuscator = "Obfuscar", confidence = "high", evidence = "Obfuscar attribute" });
        }

        // ILProtector
        if (allTypeNames.Any(n => n.Contains("ILProtector")) ||
            allResourceNames.Any(n => n.Contains("ILProtector")))
        {
            detections.Add(new { obfuscator = "ILProtector", confidence = "high", evidence = "ILProtector markers" });
        }

        // Generic obfuscation signals
        var genericSignals = new List<string>();

        // Count obfuscated names
        int obfuscatedTypeCount = module.GetTypes().Count(t => Utils.Patterns.IsObfuscatedName(t.Name?.String));
        int totalTypes = module.GetTypes().Count();
        if (obfuscatedTypeCount > totalTypes * 0.3 && totalTypes > 5)
            genericSignals.Add($"{obfuscatedTypeCount}/{totalTypes} types have obfuscated names");

        // Check for non-ASCII names (Unicode obfuscation)
        int unicodeNames = module.GetTypes().Count(t => t.Name?.String?.Any(c => c > 0x7F) == true);
        if (unicodeNames > 3)
            genericSignals.Add($"{unicodeNames} types use Unicode name obfuscation");

        // Check for empty method bodies (native method bodies / replaced by packer)
        int emptyBodies = 0;
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.HasBody && method.Body.Instructions.Count <= 1)
                    emptyBodies++;
        if (emptyBodies > 10)
            genericSignals.Add($"{emptyBodies} methods have empty/stub bodies (possible native body replacement)");

        // Check for module initializer (.cctor on <Module>)
        var moduleType = module.Types.FirstOrDefault(t => t.Name == "<Module>");
        if (moduleType?.FindStaticConstructor() != null)
            genericSignals.Add("Module initializer present (<Module>::.cctor) — common in packed assemblies");

        return new
        {
            assembly = assembly_name,
            detected_obfuscators = detections,
            generic_signals = genericSignals,
            summary = detections.Any()
                ? $"Detected: {string.Join(", ", detections.Select(d => ((dynamic)d).obfuscator))}"
                : genericSignals.Any() ? "Unknown obfuscator — generic obfuscation signals detected" : "No obfuscation detected",
        };
    }

    /// <summary>Detect control flow obfuscation patterns</summary>
    [McpServerTool, Description("Detect control flow obfuscation: switch-based dispatchers, opaque predicates, and flattened control flow. These indicate the assembly uses control flow obfuscation (ConfuserEx, .NET Reactor, etc.)")]
    public static object AnalyzeControlFlow(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Max methods to analyze (expensive operation)")] int max_methods = 500)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var suspiciousMethods = new List<object>();
        int analyzed = 0;

        foreach (var type in module.GetTypes())
        {
            foreach (var method in type.Methods)
            {
                if (!method.HasBody || analyzed >= max_methods) continue;
                analyzed++;

                var body = method.Body;
                var instrs = body.Instructions;
                if (instrs.Count < 10) continue;

                var signals = new List<string>();
                int score = 0;

                // Signal 1: High switch instruction ratio (control flow flattening)
                int switchCount = instrs.Count(i => i.OpCode == OpCodes.Switch);
                if (switchCount > 0)
                {
                    // Check if switch has many targets (dispatcher pattern)
                    foreach (var instr in instrs.Where(i => i.OpCode == OpCodes.Switch))
                    {
                        if (instr.Operand is Instruction[] targets && targets.Length > 5)
                        {
                            signals.Add($"Switch dispatcher with {targets.Length} targets");
                            score += 3;
                        }
                    }
                }

                // Signal 2: High branch-to-instruction ratio
                int branchCount = instrs.Count(i => i.OpCode.FlowControl == FlowControl.Cond_Branch ||
                                                     i.OpCode.FlowControl == FlowControl.Branch);
                double branchRatio = (double)branchCount / instrs.Count;
                if (branchRatio > 0.3 && instrs.Count > 30)
                {
                    signals.Add($"High branch ratio: {branchRatio:P0} ({branchCount}/{instrs.Count})");
                    score += 2;
                }

                // Signal 3: Repeated pattern of load-const + branch (opaque predicate)
                int constBranchPairs = 0;
                for (int i = 0; i < instrs.Count - 1; i++)
                {
                    if (IsLoadConstant(instrs[i]) && instrs[i + 1].OpCode.FlowControl == FlowControl.Cond_Branch)
                        constBranchPairs++;
                }
                if (constBranchPairs > 5)
                {
                    signals.Add($"{constBranchPairs} load-constant + conditional-branch pairs (opaque predicates)");
                    score += 2;
                }

                // Signal 4: Many local variables relative to method size (used as state variables)
                if (body.Variables?.Count > 10 && body.Variables.Count > instrs.Count / 5)
                {
                    signals.Add($"{body.Variables.Count} local variables (possible state machine)");
                    score += 1;
                }

                // Signal 5: Many exception handlers (try-catch obfuscation)
                if (body.ExceptionHandlers?.Count > 5)
                {
                    signals.Add($"{body.ExceptionHandlers.Count} exception handlers");
                    score += 1;
                }

                if (score >= 3)
                {
                    suspiciousMethods.Add(new
                    {
                        method_full_name = method.FullName,
                        declaring_type = type.FullName,
                        token = $"0x{method.MDToken.Raw:X8}",
                        score,
                        il_size = instrs.Count,
                        signals,
                    });
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            methods_analyzed = analyzed,
            cf_obfuscated_methods = suspiciousMethods.Count,
            methods = suspiciousMethods.OrderByDescending(m => ((dynamic)m).score).ToList(),
        };
    }

    /// <summary>Detect proxy/delegate-based call obfuscation</summary>
    [McpServerTool, Description("Detect proxy method and delegate-based call obfuscation. Proxy methods are thin wrappers that redirect calls to hide the real target. Delegate fields are loaded and invoked instead of direct calls. Common in ConfuserEx and custom protectors.")]
    public static object FindProxyMethods(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name,
        [Description("Max IL size to consider as proxy (default 10)")] int max_proxy_size = 10)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var proxyMethods = new List<object>();
        var delegateFields = new List<object>();

        foreach (var type in module.GetTypes())
        {
            // Detect proxy methods: tiny methods that just load args and call another method
            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;
                var instrs = method.Body.Instructions;

                // Proxy pattern: ldarg.0, ldarg.1, ..., call/callvirt, ret
                if (instrs.Count <= max_proxy_size && instrs.Count >= 2)
                {
                    var lastNonNop = instrs.LastOrDefault(i => i.OpCode != OpCodes.Nop);
                    var secondLast = instrs.Where(i => i.OpCode != OpCodes.Nop).Reverse().Skip(1).FirstOrDefault();

                    if (lastNonNop?.OpCode == OpCodes.Ret &&
                        secondLast?.Operand is IMethod target &&
                        (secondLast.OpCode == OpCodes.Call || secondLast.OpCode == OpCodes.Callvirt ||
                         secondLast.OpCode == OpCodes.Newobj))
                    {
                        // Check all preceding instructions are ldarg
                        bool allArgsLoaded = instrs
                            .Where(i => i != lastNonNop && i != secondLast && i.OpCode != OpCodes.Nop)
                            .All(i => i.OpCode.Name.StartsWith("ldarg"));

                        // Skip compiler-generated default instance constructors
                        // (they always chain to their base class .ctor — not a proxy).
                        bool isTrivialDefaultCtor = method.IsConstructor && !method.IsStatic &&
                            method.Parameters.Count == 1 && // just "this"
                            target.Name == ".ctor";

                        if (allArgsLoaded && !isTrivialDefaultCtor)
                        {
                            proxyMethods.Add(new
                            {
                                proxy_method = method.FullName,
                                real_target = target.FullName,
                                declaring_type = type.FullName,
                                token = $"0x{method.MDToken.Raw:X8}",
                                il_size = instrs.Count,
                            });
                        }
                    }
                }
            }

            // Detect delegate fields used for call obfuscation
            foreach (var field in type.Fields)
            {
                if (field.FieldType == null) continue;
                var ftName = field.FieldType.FullName ?? "";

                // Check if field type is a delegate (derives from MulticastDelegate)
                var fieldTypeDef = field.FieldType.ToTypeDefOrRef()?.ResolveTypeDef();
                bool isDelegate = fieldTypeDef?.BaseType?.FullName == "System.MulticastDelegate" ||
                                  ftName.Contains("Action") || ftName.Contains("Func") ||
                                  ftName.Contains("Delegate");

                if (isDelegate && field.IsStatic)
                {
                    // Count how many methods invoke this delegate field
                    int invocationCount = 0;
                    foreach (var m in type.Methods)
                    {
                        if (!m.HasBody) continue;
                        foreach (var instr in m.Body.Instructions)
                        {
                            if (instr.Operand is IField f && f.MDToken == field.MDToken)
                                invocationCount++;
                        }
                    }

                    if (invocationCount > 0)
                    {
                        delegateFields.Add(new
                        {
                            field_name = field.FullName,
                            field_type = ftName,
                            declaring_type = type.FullName,
                            is_static = field.IsStatic,
                            invocation_count = invocationCount,
                        });
                    }
                }
            }
        }

        return new
        {
            assembly = assembly_name,
            proxy_method_count = proxyMethods.Count,
            delegate_field_count = delegateFields.Count,
            proxy_methods = proxyMethods,
            delegate_fields = delegateFields,
        };
    }

    private static bool IsLoadConstant(Instruction instr)
    {
        return instr.OpCode == OpCodes.Ldc_I4 || instr.OpCode == OpCodes.Ldc_I4_S ||
               instr.OpCode == OpCodes.Ldc_I4_0 || instr.OpCode == OpCodes.Ldc_I4_1 ||
               instr.OpCode == OpCodes.Ldc_I4_2 || instr.OpCode == OpCodes.Ldc_I4_3 ||
               instr.OpCode == OpCodes.Ldc_I4_4 || instr.OpCode == OpCodes.Ldc_I4_5 ||
               instr.OpCode == OpCodes.Ldc_I4_6 || instr.OpCode == OpCodes.Ldc_I4_7 ||
               instr.OpCode == OpCodes.Ldc_I4_8 || instr.OpCode == OpCodes.Ldc_I4_M1;
    }
}
