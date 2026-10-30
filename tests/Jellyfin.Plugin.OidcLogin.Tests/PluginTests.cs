using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using NSubstitute;
using Xunit;

namespace Jellyfin.Plugin.OidcLogin.Tests;

public sealed class PluginTests : IDisposable
{
    private readonly string _tempDirectory = Directory.CreateTempSubdirectory("oidclogin-tests").FullName;

    private static string BuildYamlPath => Path.Combine(AppContext.BaseDirectory, "build.yaml");

    [Fact]
    public void NameMatchesBuildManifest()
    {
        Assert.Equal(ReadBuildYamlValue("name"), CreatePlugin().Name);
    }

    [Fact]
    public void IdMatchesBuildManifest()
    {
        Assert.Equal(Guid.Parse(ReadBuildYamlValue("guid")), CreatePlugin().Id);
    }

    [Fact]
    public void AssemblyIsTheBuildManifestArtifact()
    {
        var assemblyFile = typeof(Plugin).Assembly.GetName().Name + ".dll";

        Assert.Equal("Jellyfin.Plugin.OidcLogin.dll", assemblyFile);
        Assert.Contains($"- \"{assemblyFile}\"", File.ReadAllLines(BuildYamlPath).Select(l => l.Trim()));
    }

    [Fact]
    public void TargetAbiMatchesReferencedJellyfin()
    {
        var jellyfinVersion = typeof(MediaBrowser.Controller.IServerApplicationHost).Assembly.GetName().Version!;

        Assert.Equal($"{jellyfinVersion.Major}.{jellyfinVersion.Minor}.0.0", ReadBuildYamlValue("targetAbi"));
    }

    public void Dispose() => Directory.Delete(_tempDirectory, recursive: true);

    private static string ReadBuildYamlValue(string key)
    {
        var match = Regex.Match(File.ReadAllText(BuildYamlPath), $"^{key}:\\s*\"(?<value>[^\"]*)\"", RegexOptions.Multiline);
        Assert.True(match.Success, $"build.yaml has no '{key}'");
        return match.Groups["value"].Value;
    }

    private Plugin CreatePlugin()
    {
        var paths = Substitute.For<IApplicationPaths>();
        paths.PluginsPath.Returns(_tempDirectory);
        paths.PluginConfigurationsPath.Returns(_tempDirectory);
        return new Plugin(paths, Substitute.For<IXmlSerializer>());
    }
}
