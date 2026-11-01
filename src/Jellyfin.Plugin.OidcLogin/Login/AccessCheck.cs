using System;
using System.Linq;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Controller.Session;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Who may sign in: the Plugin's own settings, checked on the ID token at Callback, and Jellyfin's rules for the
/// user, checked at Complete.
/// </summary>
internal static class AccessCheck
{
    /// <summary>
    /// Enforces the Required Group (matched by name in the groups claim; an absent claim is no membership) and the
    /// required <c>amr</c>, for linked and new Subjects alike.
    /// </summary>
    /// <param name="idToken">The validated ID token.</param>
    /// <param name="configuration">The Plugin configuration.</param>
    /// <returns>The refusal, or <c>null</c> when the person may sign in.</returns>
    public static LoginFailure? Refusal(JsonWebToken idToken, PluginConfiguration configuration)
    {
        if (!string.IsNullOrEmpty(configuration.RequiredGroup)
            && !(IdTokenClaims.TryGetStrings(idToken, configuration.GroupsClaim, out var groups) && groups.Contains(configuration.RequiredGroup, StringComparer.Ordinal)))
        {
            return IdTokenClaims.Failure(idToken, configuration, FailureCategory.NotSetUp, $"The person isn't in Required Group {configuration.RequiredGroup}, or the ID token has no {configuration.GroupsClaim} claim.");
        }

        var hasAmr = IdTokenClaims.TryGetStrings(idToken, "amr", out var methods);
        if (!string.IsNullOrEmpty(configuration.RequiredAmr) && !(hasAmr && methods!.Contains(configuration.RequiredAmr, StringComparer.Ordinal)))
        {
            var used = hasAmr ? "amr " + LogText.Quote(string.Join(' ', methods!), 64) : "no amr claim";
            return IdTokenClaims.Failure(idToken, configuration, FailureCategory.PasskeyRequired, $"The sign-in didn't use the required authentication method {configuration.RequiredAmr} ({used}).");
        }

        return null;
    }

    /// <summary>
    /// The checks Jellyfin's Local Login makes and <see cref="ISessionManager.AuthenticateDirect"/> skips: the user
    /// is enabled, may connect remotely if it isn't local, and is within its parental access schedule.
    /// </summary>
    /// <param name="user">The Jellyfin user the login resolved to.</param>
    /// <param name="fromLocalNetwork">Whether the request came from one of Jellyfin's local networks.</param>
    /// <returns>The refusal, or <c>null</c> when Jellyfin would let the user sign in.</returns>
    public static LoginFailure? Refusal(User user, bool fromLocalNetwork)
    {
        if (user.HasPermission(PermissionKind.IsDisabled))
        {
            return new LoginFailure(FailureCategory.Disabled, $"Jellyfin user {user.Id} is disabled.");
        }

        if (!fromLocalNetwork && !user.HasPermission(PermissionKind.EnableRemoteAccess))
        {
            return new LoginFailure(FailureCategory.Disabled, $"Jellyfin user {user.Id} may not connect remotely.") { NotNow = true };
        }

        if (!user.IsParentalScheduleAllowed())
        {
            return new LoginFailure(FailureCategory.Disabled, $"Jellyfin user {user.Id} is outside its parental access schedule.") { NotNow = true };
        }

        return null;
    }
}
