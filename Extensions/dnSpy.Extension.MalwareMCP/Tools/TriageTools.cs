using ModelContextProtocol.Server;
using System.ComponentModel;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP.Tools;

[McpServerToolType]
public static class TriageTools
{
    /// <summary>Automated triage: suspicious APIs, IoCs, obfuscation signals</summary>
    [McpServerTool, Description("Run automated malware triage: suspicious APIs, string IoCs, P/Invoke, obfuscation detection, high-entropy resources")]
    public static object Triage(
        DnSpyBridge bridge,
        [Description("Assembly filename")] string assembly_name)
    {
        var module = bridge.FindModule(assembly_name)
            ?? throw new ArgumentException($"Assembly not found: {assembly_name}");

        var indicators = new List<object>();

        // 1. Scan TypeRefs + MemberRefs for suspicious APIs
        foreach (var mr in module.GetMemberRefs())
        {
            var fullName = mr.FullName ?? "";
            foreach (var (category, patterns) in Utils.Patterns.SuspiciousApis)
            {
                if (patterns.Any(p => fullName.Contains(p, StringComparison.OrdinalIgnoreCase)))
                {
                    indicators.Add(new { category, detail = fullName, severity = "medium" });
                }
            }
        }

        // 2. P/Invoke analysis
        var pinvokes = new List<string>();
        foreach (var type in module.GetTypes())
            foreach (var method in type.Methods)
                if (method.ImplMap != null)
                    pinvokes.Add($"{method.ImplMap.Module?.Name}!{method.ImplMap.Name}");

        if (pinvokes.Any())
            indicators.Add(new { category = "pinvoke", detail = string.Join(", ", pinvokes), severity = "medium" });

        // 3. String IoCs (regex patterns)
        var iocs = Utils.Patterns.ExtractIoCs(module);

        // 4. Obfuscation signals
        var obfSignals = new List<string>();
        foreach (var type in module.GetTypes())
        {
            if (Utils.Patterns.IsObfuscatedName(type.Name?.String))
                obfSignals.Add($"Obfuscated type: {type.FullName}");
        }
        if (obfSignals.Any())
            indicators.Add(new { category = "obfuscation", detail = $"{obfSignals.Count} obfuscated names detected", severity = "high" });

        // 5. Resource entropy
        var highEntropyResources = new List<object>();
        if (module.Resources != null)
        {
            foreach (var r in module.Resources)
            {
                if (r is EmbeddedResource er)
                {
                    var data = er.CreateReader().ToArray();
                    var entropy = Utils.Entropy.Calculate(data);
                    if (entropy > 7.0)
                    {
                        highEntropyResources.Add(new
                        {
                            name = r.Name?.String,
                            type = r.ResourceType.ToString(),
                            size = data.Length,
                            entropy = Math.Round(entropy, 1),
                        });
                    }
                }
            }
        }

        if (highEntropyResources.Any())
            indicators.Add(new
            {
                category = "high_entropy_resource",
                detail = string.Join(", ", highEntropyResources.Select(r => $"{((dynamic)r).name} ({((dynamic)r).entropy})")),
                severity = "high",
            });

        return new
        {
            assembly = module.Name?.String,
            indicator_count = indicators.Count,
            indicators,
            string_iocs = iocs,
            pinvoke_imports = pinvokes,
            high_entropy_resources = highEntropyResources,
            assembly_refs = module.GetAssemblyRefs().Select(r => r.Name?.String).ToList(),
        };
    }
}
