namespace ZZZ.Configuration;

/// <summary>
/// Single source of truth for the user-facing product version.
///
/// The value must stay in lockstep with <c>&lt;Version&gt;</c> in ZZZ.csproj and
/// the assemblyIdentity version in app.manifest; the boundary tests fail when
/// the assembly version and this constant drift apart. Everything else —
/// user-agent strings, the userscript GM_info payload, About diagnostics —
/// must reference this class instead of hard-coding a number.
/// </summary>
public static class AppVersion
{
    public const string Current = "2.2.2";

    /// <summary>Product token appended to outgoing user-agent strings, e.g. "ZZZ/2.2.2".</summary>
    public const string UserAgentProduct = "ZZZ/" + Current;
}
