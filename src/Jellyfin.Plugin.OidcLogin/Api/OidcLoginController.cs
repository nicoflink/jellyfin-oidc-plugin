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
            // A plain status until the error page arrives; the cause is only logged.
            var status = failure.Category == FailureCategory.Unavailable ? StatusCodes.Status502BadGateway : StatusCodes.Status500InternalServerError;
            return new ContentResult { StatusCode = status, ContentType = MediaTypeNames.Text.Plain, Content = $"Sign-in {failure.Category}." };
        }

        var cookie = result.FlowCookie!;
        Response.Cookies.Append(cookie.Name, cookie.Value, cookie.Options);
        return Redirect(result.AuthorizationUrl!.ToString());
    }
}
