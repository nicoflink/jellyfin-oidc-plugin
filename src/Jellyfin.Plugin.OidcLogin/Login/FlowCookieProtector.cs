using System;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Encrypts and signs the <see cref="FlowState"/> into the stateless flow cookie, with ASP.NET Core Data Protection.
/// </summary>
public sealed class FlowCookieProtector
{
    /// <summary>
    /// The flow cookie's name when the Public Jellyfin URL is https.
    /// </summary>
    public const string SecureCookieName = "__Host-OidcLogin-Flow";

    /// <summary>
    /// The flow cookie's name on loopback http, where the <c>__Host-</c> prefix (which needs <c>Secure</c>) can't be used.
    /// </summary>
    public const string InsecureCookieName = "OidcLogin-Flow";

    private readonly IDataProtector _protector;

    /// <summary>
    /// Initializes a new instance of the <see cref="FlowCookieProtector"/> class.
    /// </summary>
    /// <param name="dataProtectionProvider">Jellyfin's Data Protection provider.</param>
    public FlowCookieProtector(IDataProtectionProvider dataProtectionProvider)
    {
        _protector = dataProtectionProvider.CreateProtector("Jellyfin.Plugin.OidcLogin.FlowCookie.v1");
    }

    /// <summary>
    /// Encrypts and signs <paramref name="state"/> into a cookie value.
    /// </summary>
    /// <param name="state">The flow state.</param>
    /// <returns>The cookie value.</returns>
    public string Protect(FlowState state) => _protector.Protect(JsonSerializer.Serialize(state));

    /// <summary>
    /// Decrypts a cookie value, rejecting anything this server didn't protect or that was altered.
    /// </summary>
    /// <param name="cookieValue">The cookie value.</param>
    /// <returns>The flow state, or <c>null</c> if the value is invalid. Expiry is not checked here.</returns>
    public FlowState? Unprotect(string cookieValue)
    {
        try
        {
            return JsonSerializer.Deserialize<FlowState>(_protector.Unprotect(cookieValue));
        }
        catch (Exception e) when (e is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }
}
