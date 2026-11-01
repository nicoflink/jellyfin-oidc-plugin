using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// Why an OIDC Login didn't go ahead.
/// </summary>
/// <param name="Category">The category shown to the person.</param>
/// <param name="Cause">The admin-facing cause; never holds tokens, codes, secrets or cookies.</param>
public sealed record LoginFailure(FailureCategory Category, string Cause)
{
    /// <summary>
    /// Gets the level the failure is logged at. By default Information for Cancelled and Expired, which are normal
    /// outcomes, Error for Unavailable, and Warning for refusals and validation failures. Setup problems raise it to
    /// Error.
    /// </summary>
    public LogLevel Level { get; init; } = Category switch
    {
        FailureCategory.Cancelled or FailureCategory.Expired => LogLevel.Information,
        FailureCategory.Unavailable => LogLevel.Error,
        _ => LogLevel.Warning,
    };

    /// <summary>
    /// Gets the Subject the login was for, when the ID token was valid.
    /// </summary>
    public string? Subject { get; init; }

    /// <summary>
    /// Gets the username claim of the ID token, when it was valid and had one.
    /// </summary>
    public string? Username { get; init; }

    /// <summary>
    /// Gets a value indicating whether a Disabled refusal is only for now (remote access or the parental schedule),
    /// shown as "can't sign in right now" rather than as a disabled account.
    /// </summary>
    public bool NotNow { get; init; }

    /// <summary>
    /// Gets the reference code shown to the person and tagged on the failure's one log entry.
    /// </summary>
    public string Reference { get; init; } = string.Empty;

    /// <summary>
    /// Gets the HTTP status the failure is answered with.
    /// </summary>
    public int StatusCode => Category switch
    {
        FailureCategory.Cancelled or FailureCategory.Expired => StatusCodes.Status400BadRequest,
        FailureCategory.NotSetUp or FailureCategory.Disabled or FailureCategory.PasskeyRequired => StatusCodes.Status403Forbidden,
        FailureCategory.Unavailable => StatusCodes.Status502BadGateway,
        _ => StatusCodes.Status500InternalServerError,
    };
}
