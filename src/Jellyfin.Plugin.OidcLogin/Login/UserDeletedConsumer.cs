using System;
using System.Threading.Tasks;
using Jellyfin.Data.Events.Users;
using MediaBrowser.Controller.Events;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Removes a Jellyfin user's Linked Identity when the user is deleted, so its Subject's next login provisions afresh
/// or is refused.
/// </summary>
public sealed class UserDeletedConsumer : IEventConsumer<UserDeletedEventArgs>
{
    private readonly LinkedIdentityStore _linkedIdentities;

    /// <summary>
    /// Initializes a new instance of the <see cref="UserDeletedConsumer"/> class.
    /// </summary>
    /// <param name="linkedIdentities">The Linked Identity store.</param>
    public UserDeletedConsumer(LinkedIdentityStore linkedIdentities)
    {
        _linkedIdentities = linkedIdentities;
    }

    /// <inheritdoc />
    public Task OnEvent(UserDeletedEventArgs eventArgs)
    {
        ArgumentNullException.ThrowIfNull(eventArgs);
        _linkedIdentities.RemoveUser(eventArgs.Argument.Id);
        return Task.CompletedTask;
    }
}
