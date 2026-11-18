using Jellyfin.Plugin.OidcLogin.LocalLogin;
using Microsoft.AspNetCore.Http;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Jellyfin's local-network classification, set by the test: every request is local or none is.
/// </summary>
internal sealed class FakeLocalNetwork : ILocalNetwork
{
    public bool Local { get; set; }

    public bool IsLocal(HttpContext context) => Local;
}
