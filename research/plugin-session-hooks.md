# Research: how a Jellyfin plugin signs a user in after external authentication

Ticket: [#2](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/2) · Researched 2026-10-29

## Version baseline (surprise)

The ticket assumed 10.11.x is current stable. It is not.

- **Current stable is Jellyfin 12.4** (server and web, both tagged `v12.4`, 2026-10-26). 12.0 shipped 2026-09-08; the last 10.11 release is 10.11.11 (2026-06-06). Source: `gh release list -R jellyfin/jellyfin`, `gh release list -R jellyfin/jellyfin-web`.
- NuGet `Jellyfin.Controller` has `12.0.0` … `12.4.0`. Source: `https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json`.
- 12.x targets **net10.0** (10.11 targeted net9.0). Source: `MediaBrowser.Controller/MediaBrowser.Controller.csproj` at `v12.4` vs `v10.11.11`.
- 12.0 release notes tell users to remove third-party plugins before migrating, because "plugins will likely need time to adapt to the new database changes". Source: [v12.0 release notes](https://github.com/jellyfin/jellyfin/releases/tag/v12.0).
- `9p4/jellyfin-plugin-sso` latest is v4.0.0.4 (2026-03-20), built against `Jellyfin.Controller` **10.11.0**, `targetAbi: 10.11.0.0`, net9.0. Last commit 2026-05-11. No 12.x release. Source: `SSO-Auth/SSO-Auth.csproj`, `build.yaml`.

All Jellyfin file references below are at tag `v12.4` unless marked otherwise.

## 1. Registering endpoints (controllers) from a plugin

- On startup the server scans every loaded assembly for concrete `ControllerBase` subclasses (`ApplicationHost.GetApiPluginAssemblies`, `Emby.Server.Implementations/ApplicationHost.cs`) and adds each such assembly as an MVC application part (`Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`, `mvcBuilder.AddApplicationPart(pluginAssembly)`). So a plugin just ships a `[ApiController][Route("...")]` class; no registration call is needed.
- Authorization: the server sets a `DefaultPolicy` (applied to bare `[Authorize]`) but **no `FallbackPolicy`** (`ApiServiceCollectionExtensions.cs`). Endpoints without `[Authorize]` are anonymous, which is what the login start, callback, and token-exchange endpoints need. Admin-only endpoints use `[Authorize(Policy = Policies.RequiresElevation)]` (`MediaBrowser.Common/Api/Policies.cs`).
- DI: a plugin registers its own services through `IPluginServiceRegistrator.RegisterServices(IServiceCollection, IServerApplicationHost)` (`MediaBrowser.Controller/Plugins/IPluginServiceRegistrator.cs`, called from `PluginManager.RegisterServices`). Controllers are added "as services", so constructor injection of `ISessionManager`, `IUserManager`, etc. works.
- Static pages: `IHasWebPages.GetPages()` returns `PluginPageInfo` entries backed by embedded resources (used for the configuration page; served under `/web/configurationpage?name=...`).
- Unchanged between 10.11 and 12.4.

## 2. Turning a resolved Jellyfin user into a session: `ISessionManager.AuthenticateDirect`

- `ISessionManager` exposes `Task<AuthenticationResult> AuthenticateNewSession(AuthenticationRequest)` (password checked) and `Task<AuthenticationResult> AuthenticateDirect(AuthenticationRequest)` (**no password check**) (`MediaBrowser.Controller/Session/ISessionManager.cs`).
- Both call `AuthenticateNewSessionInternal(request, enforcePassword)` (`Emby.Server.Implementations/Session/SessionManager.cs`). With `enforcePassword = false` it:
  1. requires non-empty `App`, `AppVersion`, `DeviceId`, `DeviceName` (throws `ArgumentException` otherwise);
  2. resolves the user by `UserId`, falling back to `Username`;
  3. still enforces device access (`CanAccessDevice`) and `MaxActiveSessions`;
  4. **logs out any existing token for the same user + DeviceId**, then creates a new `Device` row whose `AccessToken` is the session token;
  5. returns `AuthenticationResult { User (UserDto), SessionInfo, AccessToken, ServerId }` and publishes `AuthenticationResultEventArgs`.
- It does **not** check `IsDisabled` or touch `InvalidLoginAttemptCount`: those live in `UserManager.AuthenticateUser`, which is skipped. A token minted for a disabled user is still rejected on every later API call (`Emby.Server.Implementations/HttpServer/Security/AuthService.cs` throws "User account has been disabled."), but the Plugin should refuse disabled users up front so the person gets a clear error rather than a dead session.
- `AuthenticationRequest` fields: `Username, UserId, Password, App, AppVersion, DeviceId, DeviceName, RemoteEndPoint` (`MediaBrowser.Controller/Session/AuthenticationRequest.cs`). Passing `RemoteEndPoint` is optional but keeps the session/activity log accurate.
- The `AuthenticateNewSessionInternal` / `GetAuthorizationToken` code is byte-identical between `v10.11.11` and `v12.4` (diffed).
- Logout: `ISessionManager.Logout(string accessToken)` revokes the token; the web client's normal sign-out already calls `POST /Sessions/Logout` (in `jellyfin-apiclient@1.11.0`), so "Logout ends only the Jellyfin session" needs no Plugin code.

## 3. `IAuthenticationProvider`: not the hook for OIDC Login

- `IAuthenticationProvider { string Name; bool IsEnabled; Task<ProviderAuthenticationResult> Authenticate(string username, string password); Task ChangePassword(User, string); }`, plus optional `IRequiresResolvedUser` and `IHasNewUserPolicy` (`MediaBrowser.Controller/Authentication/IAuthenticationProvider.cs`).
- It only runs inside `UserManager.AuthenticateUser`, which is the username+password path (`/Users/AuthenticateByName`). It is a pluggable password checker, used by LDAP-style plugins. A redirect-based OIDC Login never goes through it.
- Providers are discovered through DI (`IEnumerable<IAuthenticationProvider>` injected into `UserManager`; core registers `DefaultAuthenticationProvider` and `InvalidAuthProvider` in `Jellyfin.Server/CoreAppHost.cs`). A plugin would add one through `IPluginServiceRegistrator`.
- **Per-user provider pinning.** `User.AuthenticationProviderId` holds a provider type's `FullName`. `GetAuthenticationProviders(user)` filters enabled providers by that name. If none match, the user is assigned to **`InvalidAuthProvider`, which rejects every password login** (`Jellyfin.Server.Implementations/Users/UserManager.cs`). `CreateUserAsync` sets it to `DefaultAuthenticationProvider` (`UserManager.cs`, `new User(name, _defaultAuthenticationProvider.GetType().FullName!, ...)`).
- **Changed 10.11 → 12:** `bool HasPassword(User user)` was **removed** from `IAuthenticationProvider` (present in `v10.11.11`, absent in `v12.4`). Anything implementing the interface against 10.11 will not compile against 12.

## 4. `IUserManager` APIs the Plugin needs

From `MediaBrowser.Controller/Library/IUserManager.cs` (the file is identical in 10.11.11 and 12.4):

- `User? GetUserById(Guid)`, `User? GetUserByName(string)`, `Task<User> CreateUserAsync(string name)`, `Task UpdateUserAsync(User)`, `Task UpdatePolicyAsync(Guid, UserPolicy)`, `Task ChangePassword(Guid, string)`.
- Permissions are set on the entity, e.g. `user.SetPermission(PermissionKind.IsAdministrator, bool)`, and then saved with `UpdateUserAsync`. That is how the Admin Group sync would be done.
- **Changed in 12 (DB, not interface):** users have a `NormalizedUsername` column with a unique index (`Entities/User.cs`, `ModelConfiguration/UserConfiguration.cs`). `GetUserByName` matches `NormalizedUsername == name.ToUpperInvariant()`, so lookups are case-insensitive. The release notes say "Username capitalization can now be changed". This matters for Username Adoption: matching is case-insensitive by construction.

## 5. Getting the token into the web client after the redirect

- jellyfin-web 12.4 still uses the legacy `jellyfin-apiclient@1.11.0` `Credentials` store (`src/lib/jellyfin-apiclient/ServerConnections.js`). It persists to **`localStorage["jellyfin_credentials"]`** (default key in the package's `dist/jellyfin-apiclient.js`) as `{ Servers: [{ Id, AccessToken, UserId, ... }] }`. The device id lives at **`localStorage["_deviceId2"]`** (`src/components/apphost.js`).
- The web client has **no supported way to accept a token from a URL, query parameter, or cookie**: there is no `searchParams` / `getParameterByName` use for token, auth, or login in `src/`. Only `authenticateUserByName` and Quick Connect are first-class. So a plugin has to write the credentials into localStorage from a same-origin page.
- Same-origin works: the web client is served by the server under `/web` (`Jellyfin.Server/Startup.cs`, `RequestPath = "/web"`), so a plugin-served HTML page on the same host shares localStorage with it.
- **Changed in 12:** legacy authorization is off by default, and a migration turns it off on existing installs (`ServerConfiguration.EnableLegacyAuthorization`, `Migrations/Routines/20260531160000_DisableLegacyAuthorization.cs`). With it off, `AuthorizationContext` ignores the `X-Emby-Authorization`, `X-Emby-Token` and `X-MediaBrowser-Token` headers and the `api_key` query parameter. Only `Authorization: MediaBrowser ... Token="..."` and the `ApiKey` query parameter are honoured (`Jellyfin.Server.Implementations/Security/AuthorizationContext.cs`). Any Plugin JS that calls the API must use the `Authorization` header.

## 6. Where per-user plugin data (Subject → user) can live

- There is **no per-user plugin-data API** on `User` or in the database. The `User` entity has no extension or external-id fields. `Preferences` uses a fixed `PreferenceKind` enum (`Entities/User.cs`, `Enums/PreferenceKind.cs`).
- Options a plugin has:
  1. **Plugin configuration XML.** `BasePlugin<TConfig>.Configuration` is persisted to `PluginConfigurationsPath/<Plugin>.xml` through `SaveConfiguration()` (`MediaBrowser.Common/Plugins/BasePluginOfT.cs`). It is simple and backed up with the config, but the whole object is rewritten on each save and is shared with the admin settings UI.
  2. **Own file under `IPlugin.DataFolderPath`** (`MediaBrowser.Common/Plugins/BasePlugin.cs`), for example a JSON or SQLite store owned by the Plugin.
- 9p4 uses option 1 (see below).

## 7. How `9p4/jellyfin-plugin-sso` (v4.0.0.4, `f1e157b`) does each part

| Concern | What 9p4 does | Source |
|---|---|---|
| Endpoints | `[Route("[controller]")] SSOController : ControllerBase`. OID start `GET /sso/OID/start/{provider}`, callback `GET /sso/OID/redirect/{provider}`, token exchange `POST /sso/OID/Auth/{provider}`. Admin CRUD is under `RequiresElevation`. | `SSO-Auth/Api/SSOController.cs` |
| OIDC client | `Duende.IdentityModel.OidcClient` 6.0.1 (PKCE, discovery). State is kept in a **static in-memory `Dictionary<string, TimedAuthorizeState>`** with about 1 minute of expiry. | `SSOController.cs`, `SSO-Auth.csproj` |
| Session creation | `_sessionManager.AuthenticateDirect(new AuthenticationRequest { UserId, Username, App, AppVersion, DeviceId, DeviceName })`. App, device id and device name come from the browser. | `SSOController.Authenticate` |
| Token handoff | The callback returns an HTML page (`WebResponse.Generator`). It clears `jellyfin_credentials`, loads `/web/index.html` in a hidden sandboxed iframe so jellyfin-web initialises `_deviceId2` and `jellyfin_credentials` (busy-waits on them), then POSTs `{deviceId, appName:"Jellyfin Web", appVersion:"10.8.0", deviceName, data:state}` to `/sso/OID/Auth/{provider}`. It writes `AccessToken` and `UserId` into `jellyfin_credentials.Servers[0]`, caches the user under `user-<id>-<serverId>`, sets `enableAutoLogin`, and redirects to `/web/index.html`. | `SSO-Auth/WebResponse.cs` |
| Linked identity storage | `CanonicalLinks: SerializableDictionary<string, Guid>` per provider inside the plugin configuration XML. | `SSO-Auth/Config/PluginConfiguration.cs`, `SSOController.GetCanonicalLinks` |
| JIT provisioning / adoption | If there is no link, it looks up `GetUserByName(canonicalName)`, which **always adopts a same-named user** with no opt-in. Otherwise it calls `CreateUserAsync`, sets a random password hash, and sets `AuthenticationProviderId = GetType().FullName` (the **controller** type, not a registered provider). | `SSOController.CreateCanonicalLinkAndUserIfNotExist` |
| Admin / permissions | Sets `IsAdministrator`, folders and Live TV permissions from role claims on every login, then calls `UpdateUserAsync`. | `SSOController.Authenticate` |
| Login entry point | Not in the server. Admins paste a button into Branding → Login disclaimer that links to `/sso/OID/start/...`. | README |

Pitfalls visible in 9p4 that the Plugin should avoid:

- **`AuthenticationProviderId = "…SSOController"`** matches no registered `IAuthenticationProvider`, so provisioned users fall to `InvalidAuthProvider` and can **never** use Local Login (open upstream issue [#338](https://github.com/9p4/jellyfin-plugin-sso/issues/338)). This conflicts with our "Local Login always stays available". Leave the default provider in place.
- **Username Adoption is implicit and unconditional** (match by name). Our constraint makes it opt-in.
- The `link()` helper sends `X-Emby-Authorization`, which 12.x ignores by default (§5).
- In-memory static state does not survive a restart and is not thread-safe (plain `Dictionary` mutated from requests).
- The handoff hard-codes `appVersion "10.8.0"` and relies on `Servers[0]` and on an iframe busy-wait.
- No 12.x build exists. Users on current stable cannot run it until upstream ports to net10 and the 12 ABI.

## API changes 10.11 → 12 relevant to this Plugin

| Area | Change | Impact |
|---|---|---|
| Target framework | net9.0 → **net10.0**; NuGet `Jellyfin.Controller` 12.x | Build against 12.4 packages and net10. |
| `IAuthenticationProvider` | `HasPassword(User)` removed | Only matters if we implement the interface. We do not need to. |
| `ISessionManager` | `AuthenticateDirect` / `AuthenticateNewSession` unchanged. `AddAdditionalUser`, `RemoveAdditionalUser`, `ReportNowViewingItem` and `ReportCapabilities` gained a `controllingSessionId` parameter. | None for login. |
| `IUserManager` | Interface unchanged | None. |
| Users DB | `NormalizedUsername` + unique index, case-insensitive `GetUserByName` | Username Adoption matches case-insensitively. Just-in-time Provisioning must handle name collisions that differ only in case. |
| Auth headers | Legacy authorization off by default | Use `Authorization: MediaBrowser … Token="…"` only. |
| Plugin migration | Release notes ask users to remove third-party plugins before upgrading | Plan for "supports only latest stable", as the map already says. |

## Recommendation

1. Ship an anonymous `ControllerBase` with start, callback and complete endpoints. Do the OIDC code exchange (Authorization Code + PKCE) server-side, resolve the Linked Identity by Subject, then call **`ISessionManager.AuthenticateDirect`**. Before calling it, check `IsDisabled` yourself so the person sees a clear error; the session path itself skips that check.
2. Do **not** implement `IAuthenticationProvider`, and do not change `AuthenticationProviderId` on users. Leaving `DefaultAuthenticationProvider` keeps Local Login working.
3. Hand the token over the same way 9p4 does, with a cleaner version: the callback serves a small same-origin page. It reads or creates `_deviceId2`, POSTs a one-time, short-lived server-side code (not the OIDC `state`) to a "complete" endpoint, writes `AccessToken`/`UserId` into `jellyfin_credentials`, then redirects to `/web/`. Nothing supported is better: jellyfin-web 12.4 has no URL- or cookie-based token intake.
4. Store Linked Identities (Subject → user Guid) in a Plugin-owned store under `DataFolderPath`, not in the admin configuration XML, so that admin config saves and logins don't race. Keep pending-login state in an expiring concurrent store.
5. Build against `Jellyfin.Controller`/`Jellyfin.Model` **12.4.0** on net10.0.
