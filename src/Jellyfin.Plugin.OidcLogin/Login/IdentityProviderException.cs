using System;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// The Identity Provider answered, but not with something a login can go ahead on.
/// </summary>
public sealed class IdentityProviderException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityProviderException"/> class.
    /// </summary>
    public IdentityProviderException()
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityProviderException"/> class.
    /// </summary>
    /// <param name="message">The admin-facing cause; never holds tokens, codes or secrets.</param>
    public IdentityProviderException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="IdentityProviderException"/> class.
    /// </summary>
    /// <param name="message">The admin-facing cause; never holds tokens, codes or secrets.</param>
    /// <param name="innerException">The underlying exception.</param>
    public IdentityProviderException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
