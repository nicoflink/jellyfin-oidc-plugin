namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Container images the end-to-end tests run against, digest-pinned and bumped by Renovate.
/// </summary>
internal static class ContainerImages
{
    // renovate: datasource=docker depName=jellyfin/jellyfin
    public const string Jellyfin = "docker.io/jellyfin/jellyfin:12.4@sha256:fea863d0cd9ec445985d9c75fc6ed64e38a1d2766cf0c250a33d553f4fc1b771";

    // renovate: datasource=docker depName=ghcr.io/pocket-id/pocket-id
    public const string PocketId = "ghcr.io/pocket-id/pocket-id:v2.19.1@sha256:f085157f0b6ea4b2e8aa5ecfd99eada0d83a4960f3a3dcfd46014097d0ef7ab5";
}
