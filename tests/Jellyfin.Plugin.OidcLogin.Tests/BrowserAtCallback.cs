using System.Collections.Generic;
using Jellyfin.Plugin.OidcLogin.Login;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.OidcLogin.Tests;

/// <summary>
/// What the browser brings back to the Callback from the Identity Provider, and the authorization request's query.
/// </summary>
internal sealed record BrowserAtCallback(CallbackQuery Query, string FlowCookie, IDictionary<string, StringValues> Authorize)
{
    public void Deconstruct(out CallbackQuery query, out string flowCookie)
    {
        query = Query;
        flowCookie = FlowCookie;
    }
}
