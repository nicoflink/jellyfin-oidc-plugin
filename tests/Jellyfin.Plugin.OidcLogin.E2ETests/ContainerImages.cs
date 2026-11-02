namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// Container images the end-to-end tests run against, digest-pinned and bumped by Renovate.
/// </summary>
internal static class ContainerImages
{
    // renovate: datasource=docker depName=jellyfin/jellyfin
    public const string Jellyfin = "docker.io/jellyfin/jellyfin:12.4@sha256:fea863d0cd9ec445985d9c75fc6ed64e38a1d2766cf0c250a33d553f4fc1b771";

    // renovate: datasource=docker depName=ghcr.io/pocket-id/pocket-id
    public const string PocketId = "ghcr.io/pocket-id/pocket-id:v2.19.2@sha256:3ee23895d6a3adb5dd5de801a4fd31fc31b960e6dfbea9b5200e33cf477695eb";
}
