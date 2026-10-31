using System;
using System.Net.Mime;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.OidcLogin.Configuration;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.OidcLogin.Api;

/// <summary>
/// The admin-only endpoints behind the Plugin's configuration page.
/// </summary>
[ApiController]
[Authorize(Policy = "RequiresElevation")]
[Route("OidcLogin")]
public class OidcLoginAdminController : ControllerBase
{
    private readonly ConnectionTester _connectionTester;
    private readonly LinkedIdentityAdmin _linkedIdentities;

    /// <summary>
    /// Initializes a new instance of the <see cref="OidcLoginAdminController"/> class.
    /// </summary>
    /// <param name="connectionTester">Runs Test connection.</param>
    /// <param name="linkedIdentities">Lists, links and unlinks Linked Identities.</param>
    public OidcLoginAdminController(ConnectionTester connectionTester, LinkedIdentityAdmin linkedIdentities)
    {
        _connectionTester = connectionTester;
        _linkedIdentities = linkedIdentities;
    }

    private static Plugin Plugin => Plugin.Instance ?? throw new InvalidOperationException("The Plugin is not loaded.");

    /// <summary>
    /// Gets the configuration without the client secret, which is only reported as set or not set.
    /// </summary>
    /// <returns><c>configuration</c>, <c>clientSecretSet</c> and <c>linkedIdentityCount</c>.</returns>
    [HttpGet("Configuration")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetConfiguration()
    {
        var configuration = Plugin.Configuration;
        return Json(new
        {
            configuration,
            clientSecretSet = !string.IsNullOrEmpty(configuration.ClientSecret),
            linkedIdentityCount = _linkedIdentities.List().Count,
        });
    }

    /// <summary>
    /// Saves the configuration. An empty <c>clientSecret</c> keeps the stored one.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No content, or a bad request for a body that isn't a configuration.</returns>
    [HttpPost("Configuration")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> SaveConfiguration(CancellationToken cancellationToken)
    {
        ConfigurationUpdate? update;
        try
        {
            update = await JsonSerializer.DeserializeAsync<ConfigurationUpdate>(Request.Body, JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            update = null;
        }

        if (update?.Configuration is not { } configuration)
        {
            return BadRequest();
        }

        configuration.PublicJellyfinUrl = configuration.PublicJellyfinUrl.Trim();
        configuration.IssuerUrl = configuration.IssuerUrl.Trim();
        configuration.ClientId = configuration.ClientId.Trim();
        configuration.ClientSecret = update.ClientSecret ?? string.Empty;
        Plugin.UpdateConfiguration(configuration);
        return NoContent();
    }

    /// <summary>
    /// Test connection: fetches discovery from the given, possibly unsaved, Issuer URL.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns><c>succeeded</c>, <c>message</c> and <c>warnings</c>.</returns>
    [HttpPost("TestConnection")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> TestConnection(CancellationToken cancellationToken)
    {
        TestConnectionRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<TestConnectionRequest>(Request.Body, JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is null)
        {
            return BadRequest();
        }

        return Json(await _connectionTester.TestAsync(request.IssuerUrl ?? string.Empty, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Lists the Linked Identities.
    /// </summary>
    /// <returns>Each link's <c>issuer</c>, <c>subject</c>, <c>userId</c>, <c>userName</c>, <c>linkedAt</c> and <c>lastLoginAt</c>.</returns>
    [HttpGet("LinkedIdentities")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public ActionResult GetLinkedIdentities() => Json(_linkedIdentities.List());

    /// <summary>
    /// Links an existing Jellyfin user to a <c>sub</c> at the configured Issuer URL.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>No content, or a bad request with a <c>message</c> saying why the link was refused.</returns>
    [HttpPost("LinkedIdentities")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<ActionResult> Link(CancellationToken cancellationToken)
    {
        LinkRequest? request;
        try
        {
            request = await JsonSerializer.DeserializeAsync<LinkRequest>(Request.Body, JsonSerializerOptions.Web, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            request = null;
        }

        if (request is not { Subject: { } subject } || !Guid.TryParse(request.UserId, out var userId))
        {
            return BadRequest();
        }

        return _linkedIdentities.Link(userId, subject) is { } refusal
            ? Json(new { message = refusal }, StatusCodes.Status400BadRequest)
            : NoContent();
    }

    /// <summary>
    /// Unlinks a Jellyfin user. The user stays.
    /// </summary>
    /// <param name="userId">The Jellyfin user.</param>
    /// <returns>No content, or not found if the user wasn't linked.</returns>
    [HttpDelete("LinkedIdentities/{userId}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public ActionResult Unlink([FromRoute] Guid userId) => _linkedIdentities.Unlink(userId) ? NoContent() : NotFound();

    /// <summary>
    /// Serializes <paramref name="value"/> with fixed camelCase names, independent of Jellyfin's MVC JSON settings.
    /// </summary>
    private ContentResult Json(object value, int statusCode = StatusCodes.Status200OK)
    {
        Response.Headers.CacheControl = "no-store";
        return new ContentResult
        {
            StatusCode = statusCode,
            ContentType = MediaTypeNames.Application.Json,
            Content = JsonSerializer.Serialize(value, JsonSerializerOptions.Web),
        };
    }

    private sealed record ConfigurationUpdate(PluginConfiguration? Configuration, string? ClientSecret);

    private sealed record TestConnectionRequest(string? IssuerUrl);

    // The user id as a string: Jellyfin's API writes ids without dashes, which System.Text.Json won't read as a Guid.
    private sealed record LinkRequest(string? UserId, string? Subject);
}
