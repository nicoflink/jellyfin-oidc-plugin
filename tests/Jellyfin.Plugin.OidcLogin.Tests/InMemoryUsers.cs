using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Library;
using NSubstitute;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Jellyfin's user manager, faked over an in-memory user list. Usernames match case-insensitively, as in 12.x.
/// Administrator rights count as saved only once <see cref="IUserManager.UpdateUserAsync"/> stores them.
/// </summary>
internal sealed class InMemoryUsers
{
    private readonly List<User> _users = [];
    private readonly Dictionary<Guid, bool> _savedAdministrators = [];
    private readonly Dictionary<Guid, string> _passwords = [];

    public InMemoryUsers()
    {
        Manager = Substitute.For<IUserManager>();
        Manager.GetUserById(Arg.Any<Guid>()).Returns(call => _users.SingleOrDefault(u => u.Id == call.Arg<Guid>()));
        Manager.GetUserByName(Arg.Any<string>()).Returns(call => _users.SingleOrDefault(u => string.Equals(u.Username, call.Arg<string>(), StringComparison.OrdinalIgnoreCase)));
        Manager.CreateUserAsync(Arg.Any<string>()).Returns(call => Task.FromResult(Add(call.Arg<string>())));
        Manager.GetUsers().Returns(_ => _users.ToArray());
        Manager.UpdateUserAsync(Arg.Any<User>()).Returns(call =>
        {
            Save(call.Arg<User>());
            return Task.CompletedTask;
        });
        Manager.ChangePassword(Arg.Any<Guid>(), Arg.Any<string>()).Returns(call =>
        {
            _passwords[call.Arg<Guid>()] = call.Arg<string>();
            return Task.CompletedTask;
        });
    }

    public IUserManager Manager { get; }

    public IReadOnlyList<User> All => _users;

    public User Add(string username)
    {
        if (_users.Any(u => string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ArgumentException($"A user with the name '{username}' already exists.", nameof(username));
        }

        var user = new User(username, "Jellyfin.Server.Implementations.Users.DefaultAuthenticationProvider", "Jellyfin.Server.Implementations.Users.DefaultPasswordResetProvider");

        // As Jellyfin's CreateUserAsync does: enabled, with remote access.
        user.AddDefaultPermissions();
        _users.Add(user);
        Save(user);
        return user;
    }

    public User AddAdministrator(string username)
    {
        var user = Add(username);
        user.SetPermission(PermissionKind.IsAdministrator, true);
        Save(user);
        return user;
    }

    public bool IsSavedAdministrator(User user) => _savedAdministrators[user.Id];

    /// <summary>
    /// Gets the password last set through <see cref="IUserManager.ChangePassword"/>; null for a blank password.
    /// </summary>
    public string? PasswordOf(User user) => _passwords.GetValueOrDefault(user.Id);

    public void Remove(User user) => _users.Remove(user);

    private void Save(User user) => _savedAdministrators[user.Id] = user.HasPermission(PermissionKind.IsAdministrator);
}
