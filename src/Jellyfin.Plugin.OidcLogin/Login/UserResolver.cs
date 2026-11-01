using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The Jellyfin users behind OIDC Logins: resolves a Subject through its Linked Identity, with Just-in-time
/// Provisioning, and syncs Admin Group membership.
/// </summary>
internal sealed partial class UserResolver : IDisposable
{
    private readonly IUserManager _userManager;
    private readonly LinkedIdentityStore _linkedIdentities;
    private readonly TimeProvider _clock;
    private readonly ILogger _logger;

    /// <summary>
    /// Serializes resolving and syncing, so two concurrent first logins can't provision twice and two concurrent
    /// demotions can't both pass the last-admin guard.
    /// </summary>
    private readonly SemaphoreSlim _lock = new(1, 1);

    /// <summary>
    /// Initializes a new instance of the <see cref="UserResolver"/> class.
    /// </summary>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="linkedIdentities">The Linked Identity store.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="logger">The logger for Admin Group sync warnings.</param>
    public UserResolver(IUserManager userManager, LinkedIdentityStore linkedIdentities, TimeProvider clock, ILogger logger)
    {
        _userManager = userManager;
        _linkedIdentities = linkedIdentities;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the ID token's Subject to a Jellyfin user through its Linked Identity, provisioning a user and a link
    /// when the Subject has none (or its user is gone).
    /// </summary>
    /// <param name="idToken">The validated ID token.</param>
    /// <param name="configuration">The Plugin configuration.</param>
    /// <returns>The user, or why there is none.</returns>
    public async Task<(User? User, LoginFailure? Failure)> ResolveAsync(JsonWebToken idToken, PluginConfiguration configuration)
    {
        var issuer = idToken.Issuer;
        var subject = idToken.Subject;
        if (string.IsNullOrEmpty(subject))
        {
            return (null, new LoginFailure(FailureCategory.Failed, "The ID token has no sub claim."));
        }

        await _lock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_linkedIdentities.Find(issuer, subject) is { } link)
            {
                if (_userManager.GetUserById(link.UserId) is { } linkedUser)
                {
                    return (linkedUser, null);
                }

                // The user was deleted without the Plugin seeing the event: the link goes, as it would have then.
                _linkedIdentities.RemoveUser(link.UserId);
            }

            return await ProvisionAsync(idToken, configuration).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    /// Sets <see cref="PermissionKind.IsAdministrator"/> to Admin Group membership in <paramref name="groups"/>, and
    /// nothing else. Skipped when the setting is empty or the groups claim was absent; the last enabled administrator
    /// is never demoted. Serialized, so the admin count can't change between counting and demoting.
    /// </summary>
    /// <param name="user">The Jellyfin user signing in.</param>
    /// <param name="groups">The ID token's groups claim, or <c>null</c> when it had none.</param>
    /// <param name="configuration">The Plugin configuration.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the user is synced.</returns>
    public async Task SyncAdminGroupAsync(User user, IReadOnlyCollection<string>? groups, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(configuration.AdminGroup))
        {
            return;
        }

        if (groups is null)
        {
            LogAdminGroupSkipped(user.Id, configuration.GroupsClaim);
            return;
        }

        await _lock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var member = groups.Contains(configuration.AdminGroup, StringComparer.Ordinal);
            if (member == user.HasPermission(PermissionKind.IsAdministrator))
            {
                return;
            }

            if (!member && !_userManager.GetUsers().Any(u => u.Id != user.Id && u.HasPermission(PermissionKind.IsAdministrator) && !u.HasPermission(PermissionKind.IsDisabled)))
            {
                LogLastAdministratorKept(user.Id, configuration.AdminGroup);
                return;
            }

            user.SetPermission(PermissionKind.IsAdministrator, member);
            await _userManager.UpdateUserAsync(user).ConfigureAwait(false);
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose() => _lock.Dispose();

    /// <summary>
    /// Just-in-time Provisioning: a user named by the username claim exactly, and a Linked Identity for it.
    /// </summary>
    private async Task<(User? User, LoginFailure? Failure)> ProvisionAsync(JsonWebToken idToken, PluginConfiguration configuration)
    {
        if (!configuration.JustInTimeProvisioning)
        {
            return (null, IdTokenClaims.Failure(idToken, configuration, FailureCategory.NotSetUp, "The Subject has no Linked Identity and Just-in-time Provisioning is off."));
        }

        if (!idToken.TryGetPayloadValue<object>(configuration.UsernameClaim, out var usernameClaim) || usernameClaim is null)
        {
            return (null, IdTokenClaims.Failure(idToken, configuration, FailureCategory.NotSetUp, $"The Subject can't be provisioned: the ID token has no {configuration.UsernameClaim} claim."));
        }

        if (usernameClaim is not string username || !JellyfinUsername().IsMatch(username))
        {
            return (null, IdTokenClaims.Failure(idToken, configuration, FailureCategory.NotSetUp, $"The Subject can't be provisioned: its {configuration.UsernameClaim} claim isn't a valid Jellyfin username."));
        }

        // 12.x usernames are case-insensitive, so this also finds a case-only match. No suffix is ever tried.
        if (_userManager.GetUserByName(username) is not null)
        {
            return (null, UsernameTaken(idToken, configuration));
        }

        User user;
        try
        {
            user = await _userManager.CreateUserAsync(username).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // Jellyfin's own check: the name is taken after all, or its rules are stricter than ours.
            return (null, UsernameTaken(idToken, configuration));
        }

        // Jellyfin lets a user without a password Local Login with a blank one. This password is never stored or
        // shown, so the only way into the user is OIDC Login, until an admin sets a password.
        try
        {
            await _userManager.ChangePassword(user.Id, OidcLoginService.RandomToken()).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            // Left with a blank password, the user would be open to anyone through Local Login.
            await _userManager.DeleteUserAsync(user.Id).ConfigureAwait(false);
            return (null, IdTokenClaims.Failure(idToken, configuration, FailureCategory.Failed, $"Provisioning failed: the new Jellyfin user's password could not be set ({e.Message}), so the user was deleted again.") with { Level = LogLevel.Error });
        }

        var now = _clock.GetUtcNow();
        _linkedIdentities.Add(new LinkedIdentity(idToken.Issuer, idToken.Subject, user.Id, now, now));
        return (user, null);
    }

    private static LoginFailure UsernameTaken(JsonWebToken idToken, PluginConfiguration configuration)
        => IdTokenClaims.Failure(idToken, configuration, FailureCategory.NotSetUp, "The Subject can't be provisioned: a Jellyfin user with its username claim already exists. Link it by hand if it is the same person.");

    /// <summary>
    /// The usernames Jellyfin 12.4 accepts for a new user (<c>UserManager.ThrowIfInvalidUsername</c>).
    /// </summary>
    [GeneratedRegex(@"^(?!\s)[\w\ \-'._@+]+(?<!\s)$")]
    private static partial Regex JellyfinUsername();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Admin Group sync skipped for Jellyfin user {UserId}: the ID token has no {GroupsClaim} claim")]
    private partial void LogAdminGroupSkipped(Guid userId, string groupsClaim);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jellyfin user {UserId} is not in Admin Group {AdminGroup} but stays an administrator: it is the last one")]
    private partial void LogLastAdministratorKept(Guid userId, string adminGroup);
}
