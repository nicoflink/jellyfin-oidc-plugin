using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Jellyfin.Database.Implementations.Entities;
using MediaBrowser.Controller.Library;
using NSubstitute;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// Jellyfin's user manager, faked over an in-memory user list. Usernames match case-insensitively, as in 12.x.
/// </summary>
internal sealed class InMemoryUsers
{
    private readonly List<User> _users = [];

    public InMemoryUsers()
    {
        Manager = Substitute.For<IUserManager>();
        Manager.GetUserById(Arg.Any<Guid>()).Returns(call => _users.SingleOrDefault(u => u.Id == call.Arg<Guid>()));
        Manager.GetUserByName(Arg.Any<string>()).Returns(call => _users.SingleOrDefault(u => string.Equals(u.Username, call.Arg<string>(), StringComparison.OrdinalIgnoreCase)));
        Manager.CreateUserAsync(Arg.Any<string>()).Returns(call => Task.FromResult(Add(call.Arg<string>())));
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
        _users.Add(user);
        return user;
    }

    public void Remove(User user) => _users.Remove(user);
}
