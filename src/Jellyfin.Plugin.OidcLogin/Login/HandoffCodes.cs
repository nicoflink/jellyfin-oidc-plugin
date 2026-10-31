using System;
using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The short-lived handoff codes Callback passes to Complete: held in server memory, valid for 30 seconds,
/// usable once, and only together with the flow cookie they were minted for.
/// </summary>
public sealed class HandoffCodes
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(30);

    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;

    /// <summary>
    /// Initializes a new instance of the <see cref="HandoffCodes"/> class.
    /// </summary>
    /// <param name="clock">The clock.</param>
    public HandoffCodes(TimeProvider clock)
    {
        _clock = clock;
    }

    /// <summary>
    /// Mints a handoff code for <paramref name="userId"/>, bound to <paramref name="flowCookie"/>.
    /// </summary>
    /// <param name="userId">The Jellyfin user the login resolved to.</param>
    /// <param name="flowCookie">The flow cookie value the browser holds.</param>
    /// <returns>The handoff code.</returns>
    public string Mint(Guid userId, string flowCookie)
    {
        var now = _clock.GetUtcNow();
        foreach (var (code, grant) in _grants)
        {
            if (grant.ExpiresAt <= now)
            {
                _grants.TryRemove(code, out _);
            }
        }

        var handoffCode = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(32));
        _grants[handoffCode] = new Grant(userId, Hash(flowCookie), now + Lifetime);
        return handoffCode;
    }

    /// <summary>
    /// Consumes a handoff code. Any attempt uses the code up, so it can't be retried.
    /// </summary>
    /// <param name="handoffCode">The handoff code.</param>
    /// <param name="flowCookie">The flow cookie value presented with it.</param>
    /// <returns>The user id, or <c>null</c> if the code is unknown, used, expired or presented with another cookie.</returns>
    public Guid? Consume(string handoffCode, string flowCookie)
    {
        if (!_grants.TryRemove(handoffCode, out var grant)
            || grant.ExpiresAt <= _clock.GetUtcNow()
            || !CryptographicOperations.FixedTimeEquals(grant.FlowCookieHash, Hash(flowCookie)))
        {
            return null;
        }

        return grant.UserId;
    }

    private static byte[] Hash(string flowCookie) => SHA256.HashData(Encoding.UTF8.GetBytes(flowCookie));

    private sealed record Grant(Guid UserId, byte[] FlowCookieHash, DateTimeOffset ExpiresAt);
}
