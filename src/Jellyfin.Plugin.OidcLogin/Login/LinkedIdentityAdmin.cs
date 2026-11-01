using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.OidcLogin.Configuration;
using MediaBrowser.Controller.Library;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What an admin does with Linked Identities on the configuration page: list them, link an existing Jellyfin user
/// to a Subject by hand, and unlink.
/// </summary>
public sealed class LinkedIdentityAdmin
{
    private readonly LinkedIdentityStore _store;
    private readonly IUserManager _userManager;
    private readonly TimeProvider _clock;
    private readonly Func<PluginConfiguration> _configuration;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkedIdentityAdmin"/> class.
    /// </summary>
    /// <param name="store">The Linked Identity store.</param>
    /// <param name="userManager">Jellyfin's user manager.</param>
    /// <param name="clock">The clock.</param>
    /// <param name="configuration">Reads the current plugin configuration.</param>
    public LinkedIdentityAdmin(LinkedIdentityStore store, IUserManager userManager, TimeProvider clock, Func<PluginConfiguration> configuration)
    {
        _store = store;
        _userManager = userManager;
        _clock = clock;
        _configuration = configuration;
    }

    /// <summary>
    /// Lists every Linked Identity with the name of its Jellyfin user.
    /// </summary>
    /// <returns>The links, in the order they were made.</returns>
    public IReadOnlyList<LinkedIdentityEntry> List()
        => _store.GetAll()
            .Select(l => new LinkedIdentityEntry(l.Issuer, l.Subject, l.UserId, _userManager.GetUserById(l.UserId)?.Username, l.LinkedAt, l.LastLoginAt))
            .ToArray();

    /// <summary>
    /// Links an existing Jellyfin user to the Subject <paramref name="sub"/> at the configured Issuer URL.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <param name="sub">The <c>sub</c> claim; surrounding whitespace is ignored.</param>
    /// <returns><c>null</c> once linked, or why the link was refused.</returns>
    public string? Link(Guid userId, string sub)
    {
        ArgumentNullException.ThrowIfNull(sub);
        var issuer = _configuration().IssuerUrl;
        var subject = sub.Trim();
        if (string.IsNullOrEmpty(issuer))
        {
            return "Set the Issuer URL first: a Subject is a sub at that issuer.";
        }

        if (subject.Length == 0)
        {
            return "Enter the sub.";
        }

        if (_userManager.GetUserById(userId) is not { } user)
        {
            return "That Jellyfin user doesn't exist.";
        }

        var links = _store.GetAll();
        if (links.FirstOrDefault(l => l.Issuer == issuer && l.Subject == subject) is { } subjectLink)
        {
            return $"Subject {subject} is already linked to {UserName(subjectLink.UserId)}.";
        }

        if (links.FirstOrDefault(l => l.UserId == userId) is { } userLink)
        {
            return $"{user.Username} is already linked to Subject {userLink.Subject}.";
        }

        try
        {
            _store.Add(new LinkedIdentity(issuer, subject, userId, _clock.GetUtcNow(), null));
        }
        catch (InvalidOperationException e)
        {
            // Linked by a concurrent OIDC Login since the checks above.
            return e.Message;
        }

        return null;
    }

    /// <summary>
    /// Removes the Jellyfin user's link. The user stays; its Subject is treated as new at its next OIDC Login.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns><c>true</c> if the user was linked.</returns>
    public bool Unlink(Guid userId) => _store.RemoveUser(userId);

    private string UserName(Guid userId) => _userManager.GetUserById(userId)?.Username ?? $"deleted user {userId}";
}
