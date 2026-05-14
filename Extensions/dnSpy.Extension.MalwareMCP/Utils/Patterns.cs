using System.Text.RegularExpressions;
using dnlib.DotNet;

namespace dnSpy.Extension.MalwareMCP.Utils;

static class Patterns
{
    public static readonly Dictionary<string, string[]> SuspiciousApis = new()
    {
        ["network"] = new[] { "System.Net.WebClient", "HttpWebRequest", "TcpClient",
                              "System.Net.Http.HttpClient", "WebRequest", "Socket" },
        ["crypto"] = new[] { "Cryptography.Aes", "Cryptography.RSA", "RijndaelManaged",
                             "Cryptography.DES", "FromBase64String", "ToBase64String" },
        ["reflection"] = new[] { "Assembly.Load", "Assembly.LoadFrom", "Activator.CreateInstance",
                                 "Reflection.Emit", "DynamicMethod", "MethodInfo.Invoke" },
        ["process"] = new[] { "Process.Start", "Process.GetProcesses", "Process.Kill" },
        ["anti_analysis"] = new[] { "Debugger.IsAttached", "IsDebuggerPresent",
                                     "Environment.UserName", "Environment.MachineName",
                                     "ManagementObjectSearcher", "CheckRemoteDebuggerPresent" },
        ["persistence"] = new[] { "Microsoft.Win32.Registry", "RegistryKey", "RunOnStartup" },
        ["injection"] = new[] { "VirtualAlloc", "VirtualProtect", "WriteProcessMemory",
                                "CreateRemoteThread", "Marshal.Copy", "GetDelegateForFunctionPointer" },
        ["evasion"] = new[] { "Thread.Sleep", "Environment.TickCount", "DateTime.Now",
                              "Stopwatch", "AmsiScanBuffer" },
    };

    public static bool IsObfuscatedName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        // Very short non-alphanumeric names
        if (name.Length <= 2 && !name.All(char.IsAsciiLetterOrDigit)) return true;
        // Unicode tricks (non-ASCII non-letter chars)
        if (name.Any(c => c > 0x7F && !char.IsLetter(c))) return true;
        // Non-printable characters
        if (name.All(c => c < 0x20)) return true;
        // Names with only unprintable or control characters
        if (name.Any(c => char.IsControl(c))) return true;
        return false;
    }

    // Regex patterns for IoC extraction
    private static readonly Regex UrlPattern = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex IpPattern = new(
        @"\b(?:\d{1,3}\.){3}\d{1,3}\b",
        RegexOptions.Compiled);

    private static readonly Regex FilePathPattern = new(
        @"[A-Z]:\\[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RegistryPattern = new(
        @"(?:HKEY_[A-Z_]+|HKLM|HKCU|HKCR)\\[^\s""'<>]+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Base64Pattern = new(
        @"^[A-Za-z0-9+/]{40,}={0,2}$",
        RegexOptions.Compiled);

    private static readonly Regex EmailPattern = new(
        @"[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}",
        RegexOptions.Compiled);

    /// <summary>Extract IoCs (URLs, IPs, file paths, registry keys, base64, emails) from assembly strings</summary>
    public static object ExtractIoCs(ModuleDef module)
    {
        var urls = new HashSet<string>();
        var ips = new HashSet<string>();
        var file_paths = new HashSet<string>();
        var registry_keys = new HashSet<string>();
        var base64_strings = new HashSet<string>();
        var emails = new HashSet<string>();

        void ScanString(string s)
        {
            foreach (Match m in UrlPattern.Matches(s))
                urls.Add(m.Value);
            foreach (Match m in IpPattern.Matches(s))
            {
                // Filter out common non-IoC IPs
                var ip = m.Value;
                if (ip != "0.0.0.0" && ip != "127.0.0.1" && ip != "255.255.255.255")
                    ips.Add(ip);
            }
            foreach (Match m in FilePathPattern.Matches(s))
                file_paths.Add(m.Value);
            foreach (Match m in RegistryPattern.Matches(s))
                registry_keys.Add(m.Value);
            if (s.Length >= 40 && Base64Pattern.IsMatch(s))
                base64_strings.Add(s.Length > 100 ? s[..100] + "..." : s);
            foreach (Match m in EmailPattern.Matches(s))
                emails.Add(m.Value);
        }

        // Scan #US heap strings (walk blob-encoded stream with correct advance)
        var us = (module as ModuleDefMD)?.USStream;
        if (us != null)
        {
            var reader = us.CreateReader();
            reader.Position = 1;
            while (reader.Position < reader.Length)
            {
                if (!reader.TryReadCompressedUInt32(out uint blobLen)) break;
                if (blobLen == 0) continue;
                if (reader.Position + blobLen > reader.Length) break;
                byte[] bytes = reader.ReadBytes((int)(blobLen - 1));
                reader.ReadByte(); // terminator
                var s = System.Text.Encoding.Unicode.GetString(bytes);
                if (s.Length >= 4) ScanString(s);
            }
        }

        // Scan literal field values
        foreach (var type in module.GetTypes())
        {
            foreach (var field in type.Fields)
            {
                if (field.IsLiteral && field.Constant?.Value is string val && val.Length >= 4)
                    ScanString(val);
            }
        }

        return new
        {
            urls = urls.ToList(),
            ip_addresses = ips.ToList(),
            file_paths = file_paths.ToList(),
            registry_keys = registry_keys.ToList(),
            base64_strings = base64_strings.ToList(),
            emails = emails.ToList(),
        };
    }
}
