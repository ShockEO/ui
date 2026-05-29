// ============================================================
//  BuildInfo.cs  —  build metadata accessor
//  Reads version + build timestamp baked in by MSBuild at compile
//  time (see <Version> and <AssemblyMetadata Include="BuildTime"/>
//  in ShockUI.csproj).
// ============================================================
using System;
using System.Linq;
using System.Reflection;

namespace ShockUI;

public static class BuildInfo
{
    private static readonly Assembly _asm = typeof(BuildInfo).Assembly;

    /// <summary>Semantic version from &lt;Version&gt; in the .csproj.</summary>
    public static readonly string Version =
        _asm.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>
    /// Build timestamp injected at compile time via the
    /// "BuildTime" AssemblyMetadata attribute. Format: "yyyy-MM-dd HH:mm".
    /// </summary>
    public static readonly string BuildTime =
        _asm.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == "BuildTime")?.Value
        ?? "unknown";

    /// <summary>
    /// Friendly one-line string suitable for a status bar or footer.
    /// Example: "v0.9.0 · 2026-05-20 14:32"
    /// </summary>
    public static readonly string DisplayString = $"v{Version} · {BuildTime}";
}