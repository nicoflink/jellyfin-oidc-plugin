using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Reads the Plugin's jprm <c>build.yaml</c>, so Jellyfin gets exactly what a release ships.
/// </summary>
internal static class BuildManifest
{
    private static readonly string Yaml = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "build.yaml"));

    public static Guid PluginId { get; } = Guid.Parse(Regex.Match(Yaml, "^guid:\\s*\"(?<value>[^\"]*)\"", RegexOptions.Multiline).Groups["value"].Value);

    /// <summary>
    /// Gets the assemblies a release zip holds.
    /// </summary>
    public static IReadOnlyList<string> Artifacts { get; } = Regex.Match(Yaml, "^artifacts:\\n(?<items>(- \"[^\"]+\"\\n?)+)", RegexOptions.Multiline)
        .Groups["items"].Value
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim().TrimStart('-').Trim().Trim('"'))
        .ToArray();
}
