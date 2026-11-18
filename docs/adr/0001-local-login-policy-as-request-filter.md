# Local Login Policy is enforced by a request filter, not an authentication provider

Spec #19 kept Local Login always available as the fallback for when the Identity Provider is down. That leaves a password login open to the internet on a publicly reachable Jellyfin, so the admin can now restrict it with the Local Login Policy (`Allowed` / `LocalNetworkOnly` / `Disabled`, default `Allowed`). OIDC Login and Quick Connect are never restricted. The Plugin enforces the policy with an MVC filter that refuses both password login actions (`POST /Users/AuthenticateByName` and the legacy `POST /Users/{userId}/Authenticate`, which calls the former as a plain method and so needs its own match) with `403` before any password is checked. It does not register an `IAuthenticationProvider`, and it never changes a user's `AuthenticationProviderId`.

## Considered Options

- **Custom `IAuthenticationProvider` assigned to every user.** Rejected. It writes to every user record, so uninstalling or breaking the Plugin leaves users unable to sign in. That is the lockout that made the archived 9p4 SSO plugin unsafe. It would also check passwords before refusing, and it could not tell local requests from remote ones.
- **Per-user exceptions (break-glass admin password).** Rejected. An admin password reachable from the internet is exactly the attack surface the policy closes. `LocalNetworkOnly`, or disabling the Plugin, is the recovery path instead.

## Consequences

- Disabling or removing the Plugin restores Local Login immediately, because nothing is persisted on users.
- The policy only takes effect while the Plugin is fully configured, so a half-filled configuration can't lock anyone out. An Identity Provider outage does *not* lift it, so an attacker can't reopen Local Login by disrupting the Identity Provider.
- `LocalNetworkOnly` trusts Jellyfin's own local-network detection. A reverse proxy missing from Known Proxies makes every request look local, so the configuration page warns about it.
- If a future Jellyfin version adds another password login route, the filter must learn about it. The E2E tests cover both current routes.
