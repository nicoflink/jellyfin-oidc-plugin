using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Mime;
using System.Text;
using System.Text.Json;
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
[PluginPageHeaders]
[Route("OidcLogin")]
public class OidcLoginController : ControllerBase
{
    /// <summary>
    /// The Complete page posts a code and a device id; anything longer isn't from it.
    /// </summary>
    private const int MaxCompleteBodyLength = 4096;

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
    /// <param name="redirected">Whether Start already sent the browser to the Public Jellyfin URL's host.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to the Identity Provider, to Start on the public host, or to the error page.</returns>
    [HttpGet("Start")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<ActionResult> Start([FromQuery(Name = OidcLoginService.RedirectedToPublicHostParameter)] bool redirected, CancellationToken cancellationToken)
    {
        var request = new StartRequest(Request.Host.HasValue ? Request.Host.Value : null, redirected);
        var result = await _loginService.StartAsync(request, cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure)
        {
            return RedirectToError(failure);
        }

        if (result.PublicHostUrl is { } publicHostUrl)
        {
            return Redirect(publicHostUrl.ToString());
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
    /// <param name="error">The Identity Provider's <c>error</c>, sent instead of a code.</param>
    /// <param name="errorDescription">The Identity Provider's <c>error_description</c>.</param>
    /// <param name="iss">The RFC 9207 <c>iss</c> naming the Identity Provider that answered.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A redirect to <c>/OidcLogin/Complete</c> with a handoff code, or to the error page.</returns>
    [HttpGet("Callback")]
    [ProducesResponseType(StatusCodes.Status302Found)]
    public async Task<ActionResult> Callback(
        [FromQuery] string? code,
        [FromQuery] string? state,
        [FromQuery] string? error,
        [FromQuery(Name = "error_description")] string? errorDescription,
        [FromQuery] string? iss,
        CancellationToken cancellationToken)
    {
        var result = await _loginService.CallbackAsync(new CallbackQuery(code, state, error, errorDescription, iss), FlowCookieValue(), cancellationToken).ConfigureAwait(false);
        if (result.Failure is { } failure)
        {
            return RedirectToError(failure);
        }

        return Redirect(result.CompleteUrl!.ToString());
    }

    /// <summary>
    /// Serves the Complete page, which exchanges the handoff code in its fragment for a Jellyfin session.
    /// </summary>
    /// <returns>The Complete page.</returns>
    [HttpGet("Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CompletePage() => StaticPage("Complete.html", MediaTypeNames.Text.Html);

    /// <summary>
    /// Serves the Complete page's script.
    /// </summary>
    /// <returns>The script.</returns>
    [HttpGet("Complete.js")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult CompleteScript() => StaticPage("Complete.js", "text/javascript");

    /// <summary>
    /// Serves the page every OIDC Login failure ends on, answered with its category's status.
    /// </summary>
    /// <param name="category">The failure category; anything else is shown as Failed.</param>
    /// <param name="reference">The failure's reference code.</param>
    /// <returns>The error page.</returns>
    [HttpGet("Error")]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public ActionResult Error([FromQuery] string? category, [FromQuery(Name = "ref")] string? reference)
    {
        var failureCategory = Enum.TryParse<FailureCategory>(category, out var parsed) && Enum.IsDefined(parsed) && !int.TryParse(category, out _)
            ? parsed
            : FailureCategory.Failed;
        return new ContentResult
        {
            StatusCode = new LoginFailure(failureCategory, string.Empty).StatusCode,
            ContentType = MediaTypeNames.Text.Html + "; charset=utf-8",
            Content = ErrorPage.Render(failureCategory, reference),
        };
    }

    /// <summary>
    /// Exchanges a handoff code, presented with its flow cookie, for a Jellyfin session.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The session (<c>accessToken</c>, <c>userId</c>, <c>serverId</c>), or the failure <c>category</c> and <c>ref</c>.</returns>
    [HttpPost("Complete")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<ActionResult> Complete(CancellationToken cancellationToken)
    {
        string body;
        using (var reader = new StreamReader(Request.Body, Encoding.UTF8, leaveOpen: true))
        {
            var buffer = new char[MaxCompleteBodyLength + 1];
            var length = await reader.ReadBlockAsync(buffer, cancellationToken).ConfigureAwait(false);
            body = length > MaxCompleteBodyLength ? string.Empty : new string(buffer, 0, length);
        }

        var result = await _loginService.CompleteAsync(
            new CompleteRequest(
                Request.Headers.Origin,
                Request.ContentType,
                body,
                FlowCookieValue(),
                Request.Headers.UserAgent,
                HttpContext.Connection.RemoteIpAddress?.ToString()),
            cancellationToken).ConfigureAwait(false);

        if (result.Failure is { } failure)
        {
            return Json(failure.StatusCode, new Dictionary<string, string> { ["category"] = failure.Category.ToString(), ["ref"] = failure.Reference });
        }

        var cookie = result.ClearedFlowCookie!;
        Response.Cookies.Append(cookie.Name, cookie.Value, cookie.Options);
        var session = result.Session!;
        return Json(StatusCodes.Status200OK, new { accessToken = session.AccessToken, userId = session.UserId.ToString("N"), serverId = session.ServerId });
    }

    /// <summary>
    /// Serializes <paramref name="value"/> with fixed camelCase names, independent of Jellyfin's MVC JSON settings.
    /// </summary>
    private static ContentResult Json(int statusCode, object value) => new()
    {
        StatusCode = statusCode,
        ContentType = MediaTypeNames.Application.Json,
        Content = JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
    };

    private static ContentResult StaticPage(string name, string contentType)
    {
        using var stream = typeof(OidcLoginController).Assembly.GetManifestResourceStream("Jellyfin.Plugin.OidcLogin.Web." + name)!;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return new ContentResult { ContentType = contentType + "; charset=utf-8", Content = reader.ReadToEnd() };
    }

    /// <summary>
    /// Sends the browser to the error page, relative to <c>/OidcLogin/</c> so it holds without a configured Public
    /// Jellyfin URL. The cause is only in the reference code's log entry.
    /// </summary>
    private RedirectResult RedirectToError(LoginFailure failure)
        => Redirect("Error?category=" + failure.Category + "&ref=" + Uri.EscapeDataString(failure.Reference));

    private string? FlowCookieValue()
        => Request.Cookies[FlowCookieProtector.SecureCookieName] ?? Request.Cookies[FlowCookieProtector.InsecureCookieName];
}
