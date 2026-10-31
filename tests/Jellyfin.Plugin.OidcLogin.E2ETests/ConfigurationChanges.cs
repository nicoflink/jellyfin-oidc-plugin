using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Tests that change the shared Plugin configuration. They run alone, so no other test signs in against a
/// configuration in flux, and each puts the configuration back before it ends.
/// </summary>
[CollectionDefinition(nameof(ConfigurationChanges), DisableParallelization = true)]
public sealed class ConfigurationChanges;
