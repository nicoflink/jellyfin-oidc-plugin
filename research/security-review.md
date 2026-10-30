# Research: security review of the decided OIDC Login design

Ticket: [#17](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/17) · Reviewed 2026-10-30 · Desk review only, no code built.

Scope: the decided designs in
[#8 Design the OIDC Login flow](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/8),
[#9 Design Linked Identity storage and user lifecycle](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/9),
[#10 Prototype the Login Entry Point](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/10) and
[#12 Design failure handling for OIDC Login](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/12),
with background from #2, #4 and #5. Terms follow `GLOSSARY.md`.

## Sources

Specs and guidance:
- **RFC 9700** OAuth 2.0 Security Best Current Practice (Jan 2025): §2.1 (redirect-based flows: exact redirect URI matching, CSRF, mix-up), §2.1.1 (PKCE/nonce), §2.5 (client authentication), §2.6 (TLS), §4.1, §4.2 (Referer leakage), §4.3 (browser history), §4.4 (mix-up), §4.5 (code injection), §4.7 (CSRF), §4.8 (PKCE downgrade), §4.11 (open redirection), §4.13 (TLS-terminating reverse proxies), §4.16 (clickjacking).
- **RFC 9207** OAuth 2.0 Authorization Server Issuer Identification: §2 (`iss` in the authorization response), §2.4 (client validation).
- **OpenID Connect Core 1.0**: §3.1.2.1 (`state`, `nonce`), §3.1.3.7 (ID token validation steps 1–13), §15.5.2 (nonce implementation notes), §16 (security considerations).
- **OpenID Connect Discovery 1.0**: §4.3 (the returned `issuer` MUST be identical to the Issuer URL used to fetch the document).
- **RFC 7636** PKCE: §4.1–4.2 (verifier entropy, S256), §7.2.
- **RFC 6749**: §2.3.1 (form-urlencode credentials before HTTP Basic), §3.1.2 (redirection endpoint), §4.1.2.1 (error response carries `state`), §5.1 (`Cache-Control: no-store` on token responses), §10.12 (CSRF). **RFC 6819** §4.4.1.8 (CSRF against the redirect URI).
- **RFC 6265bis** (draft-ietf-httpbis-rfc6265bis) §4.1.3.2: `__Host-` prefix requires `Secure`, no `Domain`, `Path=/`.
- **OWASP Cheat Sheets**: Session Management (cookie attributes, `__Host-`, session ID renewal), Cross-Site Request Forgery Prevention ("Login CSRF", "Verifying Origin with standard headers", SameSite), HTML5 Security ("Local Storage": don't keep session identifiers there), Logging (log injection, data to exclude), Denial of Service.

Code (all read at the tag shown):
- Jellyfin `v12.4`: `Jellyfin.Server.Implementations/Users/UserManager.cs`, `Jellyfin.Api/Auth/DefaultAuthorizationPolicy/DefaultAuthorizationHandler.cs`, `Emby.Server.Implementations/HttpServer/Security/AuthService.cs`, `Jellyfin.Server/Configuration/CorsPolicyProvider.cs`, `Jellyfin.Server/Extensions/ApiServiceCollectionExtensions.cs`, `Jellyfin.Server/Startup.cs`, `Jellyfin.Api/Controllers/PluginsController.cs`, `Jellyfin.Data/UserEntityExtensions.cs`, `MediaBrowser.Model/Configuration/ServerConfiguration.cs`.
- jellyfin-web `v12.4`: `src/apps/legacy/controllers/session/login/index.js`.
- PocketID `v2.19.1`: `backend/internal/oidc/provider.go`, `backend/internal/controller/well_known_controller.go`, `backend/internal/utils/callback_url_util.go`, `backend/internal/service/oidc_service.go`; its fosite fork `config_default.go`.

## Jellyfin and PocketID facts this review relies on

1. **`AuthenticateDirect` skips more than `IsDisabled`.** `UserManager.AuthenticateUser` (the Local Login path, `UserManager.cs:623–649`) refuses (a) disabled users, (b) users without `EnableRemoteAccess` connecting from outside the local network, and (c) users outside their parental access schedule. The decided flow only re-checks (a). Every later API call does enforce (b) and (c) (`DefaultAuthorizationHandler.cs:59–81`), so the token is minted but useless: a dead session, not a bypass.
2. **No forwarded-header trust by default.** With `KnownProxies` empty, Jellyfin sets `ForwardedHeaders.None` (`ApiServiceCollectionExtensions.cs:171–173`). Behind a TLS-terminating proxy without that setting, `Request.Scheme` is `http` and `Request.IsHttps` is false.
3. **BaseUrl.** All of Jellyfin runs inside `app.Map(config.BaseUrl, …)` (`Startup.cs:177`), so the request `PathBase` is the BaseUrl.
4. **CORS.** `CorsHosts` defaults to `["*"]` (`ServerConfiguration.cs:236`), which becomes `AllowAnyOrigin` + any method + any header, without credentials. A configured host list becomes `WithOrigins(...).AllowCredentials()` (`CorsPolicyProvider.cs`). So by default any website can send a cross-origin `POST` to a Plugin endpoint and read the response, but the browser won't attach cookies to it.
5. **No security headers.** Jellyfin 12.4 sets no `Content-Security-Policy` or `X-Frame-Options` anywhere in the server (grep of the tree). Plugin pages get only what the Plugin sets.
6. **Login disclaimer is sanitised.** jellyfin-web renders `LoginDisclaimer` as `domPurify.sanitize(markdownIt({html:true}).render(...))` (`login/index.js:306`). Script can't run from it, but a `<form>`/`<button>` survives.
7. **Plugin config is admin-only.** `PluginsController` is `[Authorize(Policy = Policies.RequiresElevation)]` at class level (`PluginsController.cs:25`), covering `GET/POST /Plugins/{id}/Configuration`.
8. **New users get everything.** `AddDefaultPermissions` grants `EnableAllFolders` and `EnableRemoteAccess` (`UserEntityExtensions.cs:194,201`).
9. **PocketID sends `iss` in authorization responses** (RFC 9207): fosite `AuthorizationResponseIssuer: config.BaseURL` (`provider.go:46`), and discovery advertises `authorization_response_iss_parameter_supported: true` (`well_known_controller.go:116`).
10. **PocketID callback URLs may contain wildcards** (`*` in host/query, `**` in path; `callback_url_util.go:161–165`), and any port is accepted for loopback callbacks (RFC 8252 §7.3 special case, `callback_url_util.go:91–99`).
11. **PocketID can restrict a client to user groups** (`IsGroupRestricted` / `AllowedUserGroups`, `oidc_service.go:218–290, 656`).
12. **PocketID enforces PKCE only for public clients by default** (`EnforcePKCEForPublicClients: true`, plain method allowed); for a confidential client it is a per-client switch (#5).

## What the design already gets right

- **Authorization Code + PKCE S256 + confidential client + `nonce`**: covers code injection (RFC 9700 §2.1.1, §4.5) twice over.
- **`state` is 32 random bytes, single use, 10-minute TTL, and bound to the browser by a flow cookie.** That is the RFC 9700 §4.7 / RFC 6749 §10.12 CSRF defence, and it stops classic login CSRF at the callback (OWASP CSRF "Login CSRF").
- **No `returnUrl`**: no open redirector (RFC 9700 §4.11). The landing page is fixed.
- **Token never in a URL.** The handoff code sits in the fragment, so it isn't sent to servers, logs or `Referer` (RFC 9700 §4.2, §4.3). It is single use with a 30-second TTL.
- **ID token from the token endpoint, signature-checked against JWKS with an RS/ES allowlist.** That excludes `alg=none` and HS-with-public-key confusion. EdDSA is refused rather than trusted on TLS alone. `iss`, `aud`, `azp`, `exp`, `iat` and `nonce` are all checked (OIDC Core §3.1.3.7).
- **`redirect_uri` built from a configured Public Jellyfin URL**, never from the `Host` header.
- **Linked Identity keyed on issuer + `sub`; Username Adoption dropped.** Account takeover via an editable `preferred_username` is closed (OIDC Core §5.7: only `iss`+`sub` are stable).
- **Random, unrecoverable password at Just-in-time Provisioning**, so no blank-password Local Login.
- **Disabled users refused before `AuthenticateDirect`**, and username collisions look the same as "Not set up" (no user enumeration).
- **Fixed error messages and server-generated reference codes.** Nothing from the IdP is shown to the person.
- **Logs never contain tokens, codes, secrets or cookies** (OWASP Logging).
- **A fresh Jellyfin token per login**, so no session fixation of the Jellyfin token itself (OWASP Session Management "renew the session ID after authentication").
- **Lazy discovery**, so an IdP outage can't stop Jellyfin from starting, and Local Login always remains.

## Findings

Severity is for this deployment model: a self-hosted media server, one trusted Identity Provider, admins configure everything.

### Medium

#### M1. Handoff code isn't explicitly bound to the browser that ran the flow (login CSRF / session swap at `/complete`)
- **Decided element:** #8 step 3. The page POSTs the handoff code, the device id "plus the flow cookie". The resolution doesn't say the endpoint *requires* the cookie to match the flow that minted the code.
- **Risk:** an attacker finishes an OIDC Login with their own account, stops at `/complete#code=X`, and lures a victim to that URL within 30 s. Without binding, the victim's browser redeems the code and is signed in to the **attacker's** Jellyfin account: a classic login CSRF (OWASP CSRF "Login CSRF"; RFC 9700 §4.7 is the same class at the redirect URI). In Jellyfin the impact is modest: the victim's playback history goes to the attacker, and any uploads or settings they make land on the attacker's account. Because CORS defaults to `AllowAnyOrigin` (fact 4), an unbound code can also be redeemed by script on any website if it ever leaks.
- **Severity:** Medium (the 30 s window makes it low-likelihood, but it is the single gap in an otherwise complete CSRF chain).
- **Fix:** store the flow-cookie binding value with the handoff code at callback time. At `/complete`, refuse (category Expired) unless the request carries the same flow cookie, compared in constant time, and consume the code either way. Also require `Content-Type: application/json` and check that `Origin` equals the Public Jellyfin URL's origin (OWASP CSRF "Verifying Origin with standard headers").
- **Amends:** #8.

#### M2. Flow cookie `Secure` flag and name can't depend on `Request.IsHttps`
- **Decided element:** #8 flow cookie `HttpOnly; SameSite=Lax` (no `Secure`, no prefix stated); Public Jellyfin URL setting.
- **Risk:** behind the usual TLS-terminating reverse proxy with `KnownProxies` unset, Jellyfin sees plain `http` (fact 2; RFC 9700 §4.13). Deriving `Secure` from the request silently drops it, so the cookie can be sent over (or set by) plain HTTP on the same host. That weakens the browser binding M1 relies on (OWASP Session Management "Secure attribute").
- **Severity:** Medium.
- **Fix:** derive `Secure` from the **Public Jellyfin URL scheme**. When it is `https`, name the cookie `__Host-jfoidc-flow` with `Secure; Path=/; HttpOnly; SameSite=Lax` and no `Domain`. `__Host-` requires `Path=/` (RFC 6265bis §4.1.3.2), which works with any BaseUrl: it only widens the path scope of an `HttpOnly` cookie, and it doesn't collide because the name is unique. When the Public URL is `http` (the E2E harness on `http://localhost`), use a plain name, `Path=<BaseUrl>/<plugin path>`, and no `Secure`. Keep `SameSite=Lax`, not `Strict`: the callback is a cross-site top-level `GET` from the Identity Provider, so `Strict` would drop the cookie.
- **Amends:** #8.

#### M3. Just-in-time Provisioning on by default gives every Identity Provider account full library and remote access
- **Decided element:** #9 Just-in-time Provisioning default-on, "Jellyfin's standard new-user policy"; map constraint "JIT on by default".
- **Risk:** any account PocketID will sign in (including people added for other apps) gets a Jellyfin user with `EnableAllFolders` and `EnableRemoteAccess` (fact 8). That is an authorization decision handed silently to whoever manages PocketID users. PocketID can restrict the client to groups (fact 11), but nothing in the design requires or mentions it.
- **Severity:** Medium (deployment-dependent; for a family PocketID it may be intended).
- **Fix:** either (a) make the setup docs and the config page say to enable "Allowed user groups" on the PocketID client, and show a warning when Just-in-time Provisioning is on, or (b) add an optional **Required Group** setting checked against the groups claim before provisioning *and* before every OIDC Login (refusal category Not set up). (b) is cheap and IdP-agnostic. Library access from groups is already open on the map; this is the coarse gate before that.
- **Amends:** #9 (and the map constraint wording); #12 gains a refusal cause.

#### M4. Anonymous `/start` lets anyone evict every pending flow (OIDC Login DoS)
- **Decided element:** #8 pending flows in memory, cap 1,000, evict oldest.
- **Risk:** about 1,000 anonymous `GET /start` requests (trivial to send) flush every real person's pending flow. Their callbacks then fail as Expired. Local Login is unaffected, and memory stays bounded, which is good (OWASP DoS "limit resources per user"). But OIDC Login, the passkey path, is cheap to deny.
- **Severity:** Medium for availability, low for security overall.
- **Fix (pick one):** (a) **stateless flows**: keep `state`, `nonce`, the verifier and the expiry in the flow cookie itself, encrypted and authenticated with ASP.NET Core Data Protection (registered by Jellyfin's `AddMvc`; verify the key ring persists in the container, but a lost key only expires in-flight flows). This removes the cap entirely, and single use comes from deleting the cookie plus a small replay set of consumed `state` hashes until their TTL. Or (b) keep the store but add a per-client-IP cap (e.g. 10 pending flows per normalised remote IP) and evict that IP's oldest first, so one source can't evict others. Cap the handoff-code store too, even though only post-IdP requests reach it.
- **Amends:** #8.

#### M5. Missing groups claim demotes admins (Admin Group sync fails open in the destructive direction)
- **Decided element:** #9 "At every OIDC Login, `IsAdministrator` is set to whether the user is a member of the Admin Group".
- **Risk:** if the `groups` scope isn't granted, the claim name is mis-set, or PocketID omits the claim, every admin who signs in via OIDC is demoted except the last one. Combined with the race in L5, that can be all of them. This is an integrity and availability problem, not an attacker gain, but it is silent.
- **Severity:** Medium.
- **Fix:** distinguish **claim absent** from **claim present but not containing the Admin Group**. If absent (or not a string array), skip the sync for that login and log a Warning ("groups claim missing; Admin Group sync skipped"). Have the config page's Test connection check that `groups` is in `scopes_supported`.
- **Amends:** #9, #12 (new log case).

### Low

#### L1. `iss` in the authorization response is ignored (RFC 9207)
- **Decided element:** #8 callback checks flow cookie and `state` only.
- **Risk:** mix-up attacks (RFC 9700 §4.4) need a client that talks to more than one authorization server. With exactly one Identity Provider the attack doesn't apply, so RFC 9700 §2.1 doesn't require a defence. PocketID does send `iss` and advertises it (fact 9), so checking it is free and protects against a future second provider or a misconfigured issuer.
- **Severity:** Low.
- **Fix:** if discovery has `authorization_response_iss_parameter_supported: true`, **require** `iss` on the callback (success and error) and compare it exactly with the discovery issuer (RFC 9207 §2.4). If it is missing or different, the result is Failed.
- **Amends:** #8, #12 (cause under Failed).

#### L2. Discovery `issuer` not compared with the configured Issuer URL
- **Decided element:** #8 "`iss` == discovery issuer (exact)".
- **Risk:** OIDC Discovery §4.3 requires the document's `issuer` to equal the URL it was fetched from. Without that check, a wrong or hijacked discovery document can also choose the issuer that ID tokens are validated against.
- **Severity:** Low (the admin controls the URL and TLS protects the fetch).
- **Fix:** after fetching, require `discovery.issuer == configured Issuer URL` (exact string, trailing slash included). Otherwise the result is Failed/Error log, and Test connection reports it.
- **Amends:** #8.

#### L3. Insecure transport not refused for Issuer URL / Public Jellyfin URL
- **Decided element:** #8 Issuer URL and Public Jellyfin URL settings.
- **Risk:** an `http` redirect URI carries the code and `state`, and an `http` issuer carries the ID token and JWKS, in clear (RFC 9700 §2.6; RFC 6749 §3.1.2.1). Microsoft.IdentityModel's `HttpDocumentRetriever.RequireHttps` defaults to `true` and will throw for the E2E harness's `http://localhost:P`, so the implementation will need to relax it. Relaxing it globally is the trap.
- **Severity:** Low.
- **Fix:** accept `http` only for loopback hosts (`localhost`, `127.0.0.0/8`, `[::1]`), or behind an explicit "Allow insecure HTTP (testing only)" setting that logs a Warning at every login. Set `RequireHttps` from that rule. PocketID likewise gates insecure callbacks behind `AllowInsecureCallbackURLs`.
- **Amends:** #8.

#### L4. Redirect URI registration at PocketID can be loose
- **Decided element:** #8 `redirect_uri` = Public URL + callback path, shown for copy into the IdP.
- **Risk:** RFC 9700 §2.1 / §4.1 require exact string matching, but PocketID accepts wildcard patterns (fact 10). A `https://*.example.com/**` registration would let a code be delivered elsewhere. PKCE plus the client secret limit the damage (a stolen code can't be redeemed without both), but it is still a code leak.
- **Severity:** Low.
- **Fix:** setup docs and the config page say "register exactly this URL, no wildcards" and "enable PKCE enforcement on the PocketID client" (fact 12; RFC 9700 §4.8 PKCE downgrade). Send the identical `redirect_uri` in the token request (RFC 6749 §4.1.3).
- **Amends:** #8 (docs/config page wording).

#### L5. Last-admin guard is a check-then-act race
- **Decided element:** #9 "never demotes the last remaining admin".
- **Risk:** two admins removed from the Admin Group who sign in at the same time each see the other as an admin and both get demoted, leaving zero admins. Jellyfin's `UpdatePolicy` has no guard of its own (#9 fact `UserManager.cs:397`).
- **Severity:** Low.
- **Fix:** serialise Admin Group sync with a process-wide lock (the Linked Identity store's lock is fine), and recount admins inside the lock right before demoting.
- **Amends:** #9.

#### L6. Admin rights can be granted on a login that wasn't a passkey
- **Decided element:** #8 required `amr` optional, off by default; #9 Admin Group grants admin on every login.
- **Risk:** with `amr` off, a PocketID one-time link or email code (`amr=otp`) is enough to become a Jellyfin admin. That is weaker than the passkey-only premise, and compromising the person's email account is enough.
- **Severity:** Low (deliberate trade-off: one-time links are PocketID's recovery path).
- **Fix:** optional "require `amr` for admins": when the Admin Group would grant admin and `amr` lacks `phr`, either refuse (Passkey required) or sign in **without** promoting this time and log a Warning. Off by default is acceptable; document it.
- **Amends:** #8, #9.

#### L7. `AuthenticateDirect` also skips the remote-access and access-schedule checks
- **Decided element:** #8 "refuses disabled users (`AuthenticateDirect` doesn't check)".
- **Risk:** see fact 1. A user without remote access, or outside their parental schedule, gets a token that every API call then rejects. That is a confusing dead session, not a bypass.
- **Severity:** Low.
- **Fix:** before `AuthenticateDirect`, mirror `UserManager.AuthenticateUser`: refuse when `!EnableRemoteAccess && !IsInLocalNetwork(remote IP)` or `!IsParentalScheduleAllowed()`. Map both to Disabled (or a new "Not allowed now/here" category), and pass `RemoteEndPoint` in the request.
- **Amends:** #8, #12.

#### L8. Plugin pages and responses lack hardening headers
- **Decided element:** #8 static `/complete` page and the `/complete` JSON response; #12 error page.
- **Risk:** Jellyfin sets no CSP or framing headers (fact 5). The `/complete` page holds the handoff code and, briefly, the access token. Framing enables clickjacking (RFC 9700 §4.16). A cached `/complete` response would store the access token (RFC 6749 §5.1). The fragment stays in browser history (RFC 9700 §4.3).
- **Severity:** Low.
- **Fix:** on every Plugin page and response: `Cache-Control: no-store`, `Referrer-Policy: no-referrer`, `X-Content-Type-Options: nosniff`, and `Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; connect-src 'self'; form-action 'self'; frame-ancestors 'none'; base-uri 'none'` (external script file, no inline JS). The `/complete` page calls `history.replaceState` to drop `#code=…` before POSTing. It treats the fragment and the response strictly as data (no `innerHTML`). It writes only `jellyfin_credentials` and navigates.
- **Amends:** #8, #12.

#### L9. IdP error parameters: process only after `state` validation, and sanitise before logging
- **Decided element:** #12 logs the IdP's `error`/`error_description`; Cancelled on `error=access_denied`.
- **Risk:** the callback is anonymous. If `error` is acted on before the flow cookie/`state` check, anyone can mint Information/Warning log lines with attacker text. Newlines enable forged log entries (OWASP Logging "log injection"). Reflecting the text anywhere would be XSS. The decision already shows fixed messages only, which is good.
- **Severity:** Low.
- **Fix:** run the flow-cookie/`state` check first (RFC 6749 §4.1.2.1 errors carry `state`). An error with an unknown `state` is Expired, and its parameters aren't logged verbatim. For known flows, log `error` only if it is in the RFC 6749 §4.1.2.1 code set (else "other"), and `error_description` stripped of control characters and truncated (e.g. 200 chars) as a structured parameter. Never put it in HTML.
- **Amends:** #12.

#### L10. Browser-supplied device id is unvalidated input
- **Decided element:** #8 reuse `localStorage._deviceId2`.
- **Risk:** the id goes into Jellyfin's Devices table and admin UI, and drives "revoke prior token for user+device". A malicious signed-in user can only affect their own sessions, but an unbounded or odd string is stored and rendered.
- **Severity:** Low.
- **Fix:** accept only `^[A-Za-z0-9+/=_-]{1,128}$` (covers jellyfin-web's format); otherwise generate a fresh one server-side and return it for the page to store.
- **Amends:** #8.

#### L11. Client secret stored in plain text in the plugin configuration XML
- **Decided element:** #8 client secret is a plugin setting.
- **Risk:** readable by every Jellyfin admin through `GET /Plugins/{id}/Configuration` (admin-only, fact 7), echoed into the config page, and present in config backups. Leaking it alone isn't enough to redeem a code (PKCE plus the exact redirect URI), but it lets someone impersonate the client at PocketID's token endpoint.
- **Severity:** Low (admins are trusted; PocketID would need a client-impersonation scenario to matter).
- **Fix:** acceptable as is. Optionally make it write-only: the config page shows "set/unset" and a replace field, with the secret kept in a separate file under `DataFolderPath` behind a `RequiresElevation` set-only endpoint. Form-urlencode id and secret before Basic auth (RFC 6749 §2.3.1), or use `client_secret_post` if a secret has reserved characters. RFC 9700 §2.5's preference for asymmetric client auth (`private_key_jwt`) is out of reach while PocketID offers only secrets for this case. Mention it in the docs, don't build it.
- **Amends:** #8 (optional).

#### L12. Second `/start` in another tab breaks the first
- **Decided element:** #8 one flow cookie per browser.
- **Risk:** usability only. Starting twice overwrites the binding, so the first tab ends Expired. No security impact.
- **Severity:** Low (informational).
- **Fix:** name the cookie per flow (`…-flow-<first 8 chars of state hash>`) or keep a short list of bindings in one cookie, max 3.
- **Amends:** #8 (optional).

#### L13. Flow cookie lost when `/start` is reached on a different host than the Public Jellyfin URL
- **Decided element:** #8 Public Jellyfin URL; #10 button in the login page.
- **Risk:** someone on the LAN address (`http://192.168.x.y:8096`) clicks the button. The cookie is set for that host, but the IdP returns to the public host, so every such login ends Expired. With M2's `Secure` cookie on an `http` LAN origin, it can't be set at all.
- **Severity:** Low (robustness).
- **Fix:** at `/start`, if the request's scheme/host differs from the Public Jellyfin URL, first `302` to the Public URL's own `/start` (target is the configured URL, so not an open redirect), then set the cookie there. Render the Login Entry Point's `form action` as a path relative to the BaseUrl, never from the `Host` header.
- **Amends:** #8, #10.

### Confirmed non-issues

- **Branding filter keyed on the client-controlled `Client` header (#10):** cosmetic only. Spoofing it shows or hides a button. `/start` is anonymous and reachable either way, and the button carries no secret, so nothing relies on it for security. The disclaimer goes through DOMPurify (fact 6). Two hygiene points: build the button HTML from constants plus the HTML-encoded admin label only, and add `Vary: Authorization` (or `Cache-Control: no-store`) to the rewritten response so a shared cache can't serve the wrong variant. Amends #10 trivially.
- **Login CSRF at the callback:** covered by the flow cookie + `state` (see M1 for `/complete`).
- **Session fixation of the Jellyfin session:** `AuthenticateDirect` mints a new token each time. Reusing the device id replaces only *that user's* prior token on that device (#2), which is the same as a second Local Login.
- **Token in `localStorage` (XSS exposure):** forced by jellyfin-web, whose Local Login stores the same token in the same place (#2, §5). OWASP HTML5 advises against it, but the Plugin adds no new exposure beyond the brief fragment code. L8's CSP keeps the Plugin's own pages from adding XSS surface.
- **Open redirect:** none (no `returnUrl`; "Back to login" and "Try again" are fixed paths).
- **Anonymous `GET /start` forced by a third party:** it only starts a login for the victim's own Identity Provider account. That is harmless, so it is left as `GET` to keep the direct start URL working.
- **Logging PII:** Subject and username claim at Warning are proportionate for security logs (OWASP Logging lists them as acceptable identifiers). Jellyfin's `ExceptionMiddleware` logs only `Request.Path`, not the query, so the code/`state` don't reach Jellyfin logs. Reverse-proxy access logs will contain the callback query; PKCE plus the secret make that code useless.
- **ID token validation:** the decided rule set covers OIDC Core §3.1.3.7 steps 2–4, 6–7 and 9–11. Step 12 (`acr`) isn't requested and step 13 (`auth_time`/`max_age`) isn't used, so neither applies. Implementation notes: `JsonWebTokenHandler` doesn't check `nonce`, so compare it explicitly. Set `ValidAlgorithms`, `RequireSignedTokens`, `RequireExpirationTime` and `ValidateLifetime` explicitly. Require a non-empty `sub`, and treat a non-array `groups` per M5. The "one JWKS re-fetch on unknown `kid`" is rate-limited by `ConfigurationManager`'s refresh interval (5 min minimum), so it can't be used to hammer the IdP.
- **Random password at Just-in-time Provisioning:** 32+ bytes from a CSPRNG, never stored. That is sound.

## Amendments by decided ticket

| Ticket | Findings |
|---|---|
| #8 OIDC Login flow | M1, M2, M4, L1, L2, L3, L4, L6, L7, L8, L10, L11 (opt.), L12 (opt.), L13 |
| #9 Linked Identity & lifecycle | M3, M5, L5, L6 |
| #10 Login Entry Point | L13 (form action), Branding `Vary` note |
| #12 Failure handling | M3 (refusal cause), M5 (log case), L1, L7, L8, L9 |
