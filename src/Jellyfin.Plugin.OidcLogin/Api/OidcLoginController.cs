using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OidcLogin.Api;

/// <summary>
/// The anonymous OIDC Login endpoints under <c>/OidcLogin/</c>.
/// </summary>
[ApiController]
[AllowAnonymous]
[Route("OidcLogin")]
public class OidcLoginController : ControllerBase
{
    private readonly OidcLoginService _loginService;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginController"/> class.
    /// </summary>
    /// <param name="loginService">The OIDC Login service.</param>
    public OidcLoginController(OidcLoginService loginService)
    {
        _loginService = loginService;
    }

    /// <summary>
    /// Starts an OIDC Login by redirecting to the Identity Provider.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to the Identity Provider, or an error status.</returns>
    [HttpGet("Start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<ActionResult> Start(CancellationToken cancellationToken)
    {
        var result = await _loginService.StartAsync(cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure)
        {
            return PlainFailure(failure);
        }

        var cookie = result.FlowCookie!;
        Response.Cookies.Append(cookie.Name, cookie.Value, cookie.Options);
        return Redirect(result.AuthorizationUrl!.ToString());
    }

    /// <summary>
    /// Receives the browser back from the Identity Provider and hands the login over to Complete.
    /// </summary>
    /// <param name="code">The authorization code.</param>
    /// <param name="state">The <c>state</c> echoed from Start.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to <c>/OidcLogin/Complete</c> with a handoff code, or an error status.</returns>
    [HttpGet("Callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<ActionResult> Callback([FromQuery] string? code, [FromQuery] string? state, CancellationToken cancellationToken)
    {
        var flowCookie = Request.Cookies[FlowCookieProtector.SecureCookieName] ?? Request.Cookies[FlowCookieProtector.InsecureCookieName];
        var result = await _loginService.CallbackAsync(new CallbackQuery(code, state), flowCookie, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure)
        {
            return PlainFailure(failure);
        }

        return Redirect(result.CompleteUrl!.ToString());
    }

    /// <summary>
    /// A plain status until the error page arrives; the cause is only logged.
    /// </summary>
    private static ContentResult PlainFailure(LoginFailure failure) => new()
    {
        StatusCode = failure.Category switch
        {
            FailureCategory.Expired => StatusCodes.Status400BadRequest,
            FailureCategory.Unavailable => StatusCodes.Status502BadGateway,
            _ => StatusCodes.Status500InternalServerError,
        },
        ContentType = MediaTypeNames.Text.Plain,
        Content = $"Sign-in {failure.Category}.",
    };
}
