namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// A person seeded at PocketID. The fixed <see cref="Id"/> is their <c>sub</c> claim.
/// </summary>
internal sealed record PocketIdUser(string Id, string Username);
