using Xunit;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Tests that stop PocketID. They run alone, after every other test, so no other login sees the outage.
/// </summary>
[CollectionDefinition(nameof(IdentityProviderOutage), DisableParallelization = true)]
public sealed class IdentityProviderOutage
{
}
