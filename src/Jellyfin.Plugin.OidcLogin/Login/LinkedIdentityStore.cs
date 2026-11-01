using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Keeps the Linked Identities in a Plugin-owned JSON file, written atomically (temp file plus rename) under a lock.
/// Links are strictly one-to-one: a Subject links to at most one user and a user to at most one Subject.
/// </summary>
public sealed class LinkedIdentityStore
{
    private const string FileName = "linked-identities.json";

    private readonly string _path;
    private readonly Lock _lock = new();
    private List<LinkedIdentity>? _links;

    /// <summary>
    /// Initializes a new instance of the <see cref="LinkedIdentityStore"/> class.
    /// </summary>
    /// <param name="dataFolder">The Plugin's data folder; created on first write.</param>
    public LinkedIdentityStore(string dataFolder)
    {
        _path = Path.Combine(dataFolder, FileName);
    }

    /// <summary>
    /// Gets every Linked Identity.
    /// </summary>
    /// <returns>The links.</returns>
    public IReadOnlyList<LinkedIdentity> GetAll()
    {
        lock (_lock)
        {
            return Load().ToArray();
        }
    }

    /// <summary>
    /// Finds the link for a Subject.
    /// </summary>
    /// <param name="issuer">The issuer.</param>
    /// <param name="subject">The <c>sub</c> claim.</param>
    /// <returns>The link, or <c>null</c> if the Subject isn't linked.</returns>
    public LinkedIdentity? Find(string issuer, string subject)
    {
        lock (_lock)
        {
            return Load().SingleOrDefault(l => IsSubject(l, issuer, subject));
        }
    }

    /// <summary>
    /// Adds a link.
    /// </summary>
    /// <param name="link">The link.</param>
    /// <exception cref="InvalidOperationException">The Subject or the user is already linked.</exception>
    public void Add(LinkedIdentity link)
    {
        ArgumentNullException.ThrowIfNull(link);
        lock (_lock)
        {
            var links = Load();
            if (links.Any(l => IsSubject(l, link.Issuer, link.Subject) || l.UserId == link.UserId))
            {
                throw new InvalidOperationException("The Subject or the Jellyfin user is already linked.");
            }

            Save([.. links, link]);
        }
    }

    /// <summary>
    /// Removes the link of a Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user's id.</param>
    /// <returns><c>true</c> if the user was linked.</returns>
    public bool RemoveUser(Guid userId)
    {
        lock (_lock)
        {
            var links = Load();
            var kept = links.Where(l => l.UserId != userId).ToList();
            if (kept.Count == links.Count)
            {
                return false;
            }

            Save(kept);
            return true;
        }
    }

    /// <summary>
    /// Records a sign-in of a linked Jellyfin user.
    /// </summary>
    /// <param name="userId">The Jellyfin user's id.</param>
    /// <param name="at">When the user signed in.</param>
    public void RecordLogin(Guid userId, DateTimeOffset at)
    {
        lock (_lock)
        {
            Save(Load().Select(l => l.UserId == userId ? l with { LastLoginAt = at } : l).ToList());
        }
    }

    private static bool IsSubject(LinkedIdentity link, string issuer, string subject)
        => string.Equals(link.Issuer, issuer, StringComparison.Ordinal) && string.Equals(link.Subject, subject, StringComparison.Ordinal);

    private List<LinkedIdentity> Load()
    {
        if (_links is null)
        {
            _links = File.Exists(_path)
                ? JsonSerializer.Deserialize<List<LinkedIdentity>>(File.ReadAllText(_path)) ?? []
                : [];
        }

        return _links;
    }

    private void Save(List<LinkedIdentity> links)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tempPath = _path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(links));
        File.Move(tempPath, _path, overwrite: true);
        _links = links;
    }
}
