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

    /// <summary>
    /// Gets the files for <see cref="Artifacts"/> in <paramref name="directory"/>, failing loudly when one is
    /// missing: Jellyfin would otherwise just not load the Plugin, and the fixture would fail later with a 404.
    /// </summary>
    /// <param name="directory">The build output holding the Plugin and its dependencies.</param>
    /// <returns>The artifact files.</returns>
    public static IReadOnlyList<FileInfo> ArtifactFiles(string directory)
    {
        var files = Artifacts.Select(artifact => new FileInfo(Path.Combine(directory, artifact))).ToArray();
        var missing = files.Where(file => !file.Exists).Select(file => file.Name).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"build.yaml lists artifacts missing from {directory}: {string.Join(", ", missing)}. "
                + "Rebuild with `dotnet build --no-incremental`; an incremental build can leave dependencies uncopied.");
        }

        return files;
    }
}
