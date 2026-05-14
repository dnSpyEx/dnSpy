using dnlib.DotNet;
using dnlib.DotNet.MD;

namespace dnSpy.Extension.MalwareMCP.Utils;

/// <summary>
/// Resolves metadata tokens to human-readable names.
/// Useful for resolving operands in IL instructions.
/// </summary>
static class TokenResolver
{
    /// <summary>Resolve a metadata token (0x06000001 etc.) to its definition</summary>
    public static string? ResolveToken(ModuleDef module, uint token)
    {
        var mdToken = new MDToken(token);
        var md = module as ModuleDefMD;
        if (md == null) return $"Unknown(0x{token:X8})";
        return mdToken.Table switch
        {
            Table.TypeDef => md.ResolveTypeDef(mdToken.Rid)?.FullName,
            Table.TypeRef => md.ResolveTypeRef(mdToken.Rid)?.FullName,
            Table.Method => md.ResolveMethod(mdToken.Rid)?.FullName,
            Table.Field => md.ResolveField(mdToken.Rid)?.FullName,
            Table.MemberRef => md.ResolveMemberRef(mdToken.Rid)?.FullName,
            Table.TypeSpec => md.ResolveTypeSpec(mdToken.Rid)?.FullName,
            Table.MethodSpec => md.ResolveMethodSpec(mdToken.Rid)?.FullName,
            Table.StandAloneSig => $"StandAloneSig(0x{token:X8})",
            _ => $"Unknown(0x{token:X8})",
        };
    }

    /// <summary>Format a metadata token for display</summary>
    public static string FormatToken(IMDTokenProvider? provider)
    {
        if (provider == null) return "null";
        return $"0x{provider.MDToken.Raw:X8}";
    }

    /// <summary>Get table name from token</summary>
    public static string GetTableName(uint token)
    {
        return new MDToken(token).Table.ToString();
    }
}
