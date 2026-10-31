using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.OidcLogin.Login;

/// <summary>
/// What Callback hands over to Complete through a handoff code.
/// </summary>
/// <param name="UserId">The Jellyfin user the login resolved to.</param>
/// <param name="Groups">The ID token's groups claim, for Admin Group sync; <c>null</c> when it had none.</param>
public sealed record Handoff(Guid UserId, IReadOnlyCollection<string>? Groups);
