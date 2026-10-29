# Research: end-to-end test harness (Jellyfin + PocketID + virtual authenticator)

Ticket: [#6](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/6). Desk research, 2026-10-29. Nothing below was run; the "spike" items at the end are the parts to check first in code.

## Question

Can a fully automated login test run in CI: a Jellyfin container, a PocketID container, and a headless browser with a virtual WebAuthn authenticator? The ticket covers seeding PocketID headlessly, unattended Jellyfin first-run setup, Testcontainers vs docker compose, and running on GitHub Actions. If this is not feasible, which mock OIDC provider should we fall back to?

## Answer

**Yes, it is feasible, and we do not need a mock provider.** Every piece is supported by first-party features in the current releases:

| Piece | Mechanism | Source |
|---|---|---|
| PocketID admin access without a UI | `STATIC_API_KEY` env var, sent in the `X-API-Key` header | pocket-id `env_config.go`, `apikey/service.go`, `middleware/authenticator.go` |
| Users, groups, OIDC client | `POST /api/users`, `POST /api/user-groups`, `POST /api/oidc/clients` (client `id` can be chosen) | pocket-id controllers and DTOs |
| Logging in as the seeded user to enrol a passkey | `POST /api/users/:id/one-time-access-token`, then the browser opens `/lc/<token>` | pocket-id `onetimeaccess` package, `frontend/src/routes/lc` |
| Virtual authenticator | Playwright `BrowserContext.Credentials` (new in v1.61, works in .NET), or the raw CDP `WebAuthn.*` domain (Chromium only) | Playwright `class-credentials.md`, `class-cdpsession.md` |
| Jellyfin first-run setup | `/Startup/*` endpoints, open while the wizard is incomplete | jellyfin `StartupController.cs` |
| Orchestration | Testcontainers for .NET 4.16 inside `dotnet test` | testcontainers-dotnet |
| CI | `ubuntu-24.04` runners already include Docker 28, Chrome and the .NET 10 SDK | actions/runner-images |

The one real constraint is **networking**. WebAuthn needs a secure context, so PocketID's `APP_URL` (which sets the passkey RP ID and origin) must be `http://localhost:<port>`. Jellyfin's back channel (discovery, token, JWKS) must also reach the **same issuer URL**. Recommended fix: put Jellyfin in PocketID's network namespace, described below.

## Versions checked (2026-10-29)

- PocketID **v2.19.1** (2026-10-22), image `ghcr.io/pocket-id/pocket-id:v2.19.1`, default port 1411
- Jellyfin **12.4** (2026-10-26), image `jellyfin/jellyfin:12.4` (amd64 and arm64), built for `net10.0`. NuGet `Jellyfin.Controller` 12.4.0
- Microsoft.Playwright (.NET) **1.64.1** (2026-10-22)
- Testcontainers for .NET **4.16.0** (2026-10-09)
- GitHub `ubuntu-24.04` runner image: Docker Server/Client 28.0.4, Compose 2.38.2, Chrome 156, .NET SDK 10.0.x
- Fallback mock: navikt/mock-oauth2-server 6.1.4 (2026-10-28). Soluto/oidc-server-mock was last released in May 2025 (v0.12.1) and looks stale.

## 1. Seeding PocketID headlessly

### Admin credential: `STATIC_API_KEY`

- `EnvConfigSchema.StaticApiKey` is read from `STATIC_API_KEY` (a `_FILE` variant also works) and must be at least 16 characters (`backend/internal/common/env_config.go`).
- The docs describe it as "an admin-rights key for declarative setups". It acts as a synthetic admin, "Static API User", with the fixed ID `00000000-0000-0000-0000-000000000000`, created the first time the key is used (`backend/internal/apikey/service.go` `initStaticApiKeyUser`). Source: <https://pocket-id.org/docs/configuration/environment-variables>.
- The header is `X-API-Key` (`backend/internal/middleware/authenticator.go`, `apiKeyHeader`).
- The static user does not count as a real user for initial setup (`usersignup/service.go` filters `id != StaticApiKeyUserID`). The `/setup` initial-admin page therefore stays open, but it is harmless and we never need it. **We do not need to create an initial admin.**
- The only other required variable is `ENCRYPTION_KEY` (16 bytes or more), per the docs.

### Objects the test fixture creates, all through `X-API-Key`

1. **Admin Group and an ordinary group**: `POST /api/user-groups` with `{ "name", "friendlyName" }` (`dto/user_group_dto.go`).
2. **Users**: `POST /api/users` with `{ "id"? (uuid), "username", "email", "emailVerified", "firstName", "lastName", "displayName", "userGroupIds": [...] }` (`dto/user_dto.go`). Because we can choose the `id`, the **Subject** (`sub`) is known in advance, which makes assertions on the Linked Identity easy. Group membership can also be changed later with `PUT /api/users/:id/user-groups`.
3. **OIDC client**: `POST /api/oidc/clients` with `{ "id": "jellyfin", "name", "callbackURLs": [...], "isPublic", "pkceEnabled": true, "skipConsent": true, ... }` (`dto/oidc_dto.go`, `OidcClientCreateDto` embeds `OidcClientUpdateDto` plus `id`). Use `skipConsent: true` so the test never meets a consent screen. For a confidential client, `POST /api/oidc/clients/:id/secrets` takes `{ "secret": "<own value, 16 chars or more>" }`, so the secret can be fixed in advance too.
   - **Allowed groups**: send `"isGroupRestricted": true` in the create body, then `PUT /api/oidc/clients/:id/allowed-user-groups` with `{ "userGroupIds": [...] }` (`dto/oidc_dto.go` `OidcUpdateAllowedUserGroupsDto`, `oidc_controller.go`). With restriction off (the default `false`), any PocketID user may authorize. Restriction on gives a free negative test: a user outside the allowed groups is rejected at PocketID (useful for the Failure UX ticket).
   - **PKCE toggle**: `pkceEnabled` (plus `isPublic` for a public client without a secret).
   - **Exact callback URL plus random Jellyfin port**: PocketID implements RFC 8252 §7.3. For `http` callbacks on `localhost` or a loopback IP, **any port matches** a registered URL that has no port (`utils/callback_url_util.go` `GetCallbackURLFromList`, `loopbackURLWithWildcardPort`). So register `http://127.0.0.1/<plugin callback path>` once, and Jellyfin's random host port needs no client update. Outside loopback, callbacks must match exactly (wildcard patterns are also supported by `matchCallbackURL`).
   - The whole client can be created declaratively at fixture start, so there is no admin UI step.
4. **Callback URLs**: `http://localhost…` / `http://127.0.0.1…` callbacks are accepted without any flag. PocketID's redirect checker defers to fosite's `IsRedirectURISecure`, which allows plain HTTP for `localhost`, `*.localhost` and loopback IPs (`oidc/provider.go`, `ory/fosite authorize_helper.go` `IsLocalhost`). `ALLOW_INSECURE_CALLBACK_URLS=true` is needed only for other HTTP hosts.

### Enrolling a passkey for the seeded user

There is **no admin API that adds a passkey**. Passkeys are created only through the WebAuthn ceremony (`GET /api/webauthn/register/start`, `POST /api/webauthn/register/finish`, which need an authenticated session). Approach:

1. `POST /api/users/:id/one-time-access-token` (optional `ttl`) returns a login code. Admin-issued tokens are **not** gated by the email-login app settings. Only the email variants check `EmailOneTimeAccess*Enabled` (`onetimeaccess/service.go`).
2. In a Playwright context with the virtual authenticator installed, open `http://localhost:<P>/lc/<token>`, which redirects to `/login/alternative/code?code=…` and signs the user in.
3. Open `/settings/account` and click **"Add Passkey"** (`frontend/src/routes/settings/account/+page.svelte` calls `@simplewebauthn/browser` `startRegistration`). The virtual authenticator answers.
4. Read the credential back (`context.Credentials.GetAsync(rpId: "localhost")`) and close the context. Then run the actual **OIDC Login** test in a *fresh* context seeded with that credential (`Credentials.CreateAsync(...)`). This forces a real passkey assertion at PocketID, not just a reused PocketID session cookie.

PocketID's own E2E suite does the same thing (`tests/utils/passkey.util.ts`: CDP `WebAuthn.enable`, `WebAuthn.addVirtualAuthenticator {protocol: ctap2, transport: internal, hasResidentKey, hasUserVerification, isUserVerified}`, `WebAuthn.addCredential`). It differs in one respect: it pre-seeds the credentials into the database through a `/api/test/reset` endpoint. **That endpoint exists only in builds tagged `e2etest`** (`//go:build e2etest` on `controller/e2etest_controller.go` and `service/e2etest_service.go`), so it is **not** in the published image. We must use the API + UI enrolment above. We could build our own PocketID image with `-tags e2etest` (pocket-id's `tests/setup` does this for its own suite), but `/test/reset` loads PocketID's *own* fixed fixture data (`service/e2etest_service.go`: users like "tim"/"craig", clients with fixed callbacks), not ours. It would also put a source build of PocketID into our CI and outside Renovate's image tracking. **Verdict: the public API with `STATIC_API_KEY` is the scriptable path. Do not use the `e2etest` controller.** `APP_ENV=test` in a normal build only relaxes rate limiting (`middleware/rate_limit.go`) and sets gin to test mode. It is optional, but it does help a fast test loop.

## 2. Virtual authenticator in Playwright (.NET)

Two options, both from first-party docs:

- **`BrowserContext.Credentials` (Playwright 1.61 or later, recommended).** A virtual WebAuthn authenticator scoped to a context, with C# samples in the docs. `InstallAsync()` overrides `navigator.credentials.create/get` in all current and future pages and must be called before navigation. `CreateAsync(rpId, {Id, UserHandle, PrivateKey, PublicKey, SignCount})` seeds a credential, and `GetAsync({RpId})` returns credentials the page registered, private key included. The capture-then-reseed pattern is documented explicitly. Sources: `docs/src/api/class-credentials.md` in microsoft/playwright (PR #40849, merged 2026-05-16, which closed the long-standing feature request #7276).
  - It is a JS shim (`packages/injected/src/webAuthn.ts`) that bridges to a Node-side authenticator, so it does not depend on the browser engine. It still needs a secure context: `@simplewebauthn/browser` checks `window.PublicKeyCredential`, which browsers expose only on secure origins. Hence the localhost requirement holds either way.
- **Raw CDP `WebAuthn` domain** through `Context.NewCDPSessionAsync(page)`. Chromium only. This is PocketID's own approach and a proven fallback if the shim surprises us.

## 3. The networking problem and the recommended topology

Constraints, each from source:

- PocketID sets `RPID = hostname(APP_URL)` and `RPOrigins = [APP_URL]` (`webauthn/service.go`). Passkeys are bound to that domain.
- WebAuthn and `PublicKeyCredential` need a secure context. With plain HTTP, that means the browser must reach PocketID as `localhost` (or `127.0.0.1`).
- The discovery `issuer` and `authorization_endpoint` always use `APP_URL`. Only `token_endpoint`, `userinfo_endpoint`, `jwks_uri`, `introspection_endpoint` and the PAR endpoint switch to `INTERNAL_APP_URL` (`controller/well_known_controller.go`). The docs say the variable is for apps that reach Pocket ID at "another address than browsers".
- The Plugin is configured with one issuer and fetches `<issuer>/.well-known/openid-configuration` from inside the Jellyfin container. A plain Docker bridge network fails here: `localhost` inside the Jellyfin container is Jellyfin itself.

Options considered:

| Option | Verdict |
|---|---|
| **A. Jellyfin joins PocketID's network namespace** (`HostConfig.NetworkMode = "container:<pocketid-id>"`, or `network_mode: service:pocketid` in compose). Inside Jellyfin, `localhost:<P>` *is* PocketID. | **Recommended.** Works on Docker Desktop too. No TLS, no DNS tricks. |
| B. `--network host` for Jellyfin (and publish PocketID's port) | Works on Linux CI. Not portable to Docker Desktop for local runs, and leaks onto host ports. |
| C. `APP_URL=http://localhost:P` + `INTERNAL_APP_URL=http://pocketid:1411`, with the Plugin given a separate "metadata/back-channel URL" | Would need an extra Plugin setting purely for tests. Discovery itself still has to be fetched from somewhere. Rejected unless the real deployment needs it (see open questions). |
| D. Docker alias `pocketid.localhost` (browsers resolve `*.localhost` to loopback) | Fragile. .NET's resolver has RFC 6761 `*.localhost` → loopback fallbacks (dotnet/runtime #128068 merged 2026-05-20, #130504), so behaviour inside Jellyfin may change across .NET versions. Rejected. |
| E. HTTPS with a self-signed cert (PocketID supports `TLS_CERT`/`TLS_KEY`) | Chromium disables WebAuthn on certificate errors, so we would have to inject a trusted CA into the browser and into Jellyfin. Too much moving machinery. |

**Topology for option A:**

```
host (test process + Playwright Chromium)
 ├─ http://localhost:P        ─┐  published P:P (hostPort == containerPort)
 └─ http://127.0.0.1:J        ─┤  published random:8096
                               ▼
   ┌─────────── one network namespace ───────────┐
   │  pocketid  (PORT=P, APP_URL=http://localhost:P)
   │  jellyfin  (NetworkMode=container:pocketid, listens 8096)
   └─────────────────────────────────────────────┘
```

- The PocketID container is the namespace owner, so **it** publishes both ports. Jellyfin has none of its own. The Jellyfin host port is `pocketId.GetMappedPublicPort(8096)`.
- PocketID's port must have **hostPort == containerPort**. Otherwise the URL that the browser and Jellyfin use (`localhost:P`) differs from `APP_URL`. Pick a free port P in the fixture, then set `PORT=P`, `APP_URL=http://localhost:P` and `WithPortBinding(P, P)`. Do not hard-code 1411, so parallel/local runs don't collide.
- Have the browser reach **Jellyfin at `127.0.0.1:J`** and **PocketID at `localhost:P`**. Cookies are not port-scoped, so putting both apps on host `localhost` would let their cookies mix. Using two hostnames keeps cookie jars separate, and both are secure contexts. Thanks to PocketID's loopback any-port rule, the client can register `http://127.0.0.1/<callback path>` before J is known.
- Testcontainers for .NET has **no `WithNetworkMode` builder method** (checked in 4.16.0 source), but `WithCreateParameterModifier(p => p.HostConfig.NetworkMode = $"container:{pocketId.Id}")` is the generic escape hatch (`Builders/AbstractBuilder\`4.cs`). Wait strategies for Jellyfin must target the owner container's mapped port, or use an HTTP wait on `/health` through it.

## 4. Unattended Jellyfin first-run setup

`Jellyfin.Api/Controllers/StartupController.cs` (v12.4), class policy `FirstTimeSetupOrElevated`. The endpoints are anonymous until `IsStartupWizardCompleted` is set:

1. `POST /Startup/Configuration` with `{ ServerName, UICulture, MetadataCountryCode, PreferredMetadataLanguage }`
2. `GET /Startup/User` (returns the auto-created first user)
3. `POST /Startup/User` with `{ Name, Password }`. Returns 403 if a password is already set, 400 if the password is empty.
4. `POST /Startup/RemoteAccess` with `{ EnableRemoteAccess }`
5. `POST /Startup/Complete`, which sets `IsStartupWizardCompleted = true`

After that, `POST /Users/AuthenticateByName` returns an admin token for configuring the Plugin (`/Plugins/{id}/Configuration`) and setting the Branding login disclaimer (the baseline Login Entry Point).

**Plugin installation:** in the official image, `JELLYFIN_DATA_DIR=/config`, so plugins load from `/config/plugins/<Name>_<version>/` (jellyfin-packaging `docker/Dockerfile`). Copy the built Plugin DLL plus `meta.json` there with `WithResourceMapping(...)` **before** the container starts. That avoids the restart a runtime install would need. The image has `HEALTHCHECK` on `http://localhost:8096/health`, which we reuse as the wait strategy.

## 5. Testcontainers for .NET vs docker compose

| | Testcontainers (.NET) | docker compose |
|---|---|---|
| Lives in | `dotnet test` (xUnit fixture) | external `docker compose up` step + test |
| Dynamic ports / per-run isolation | yes, plus Ryuk cleanup | manual |
| Netns sharing (option A) | via `WithCreateParameterModifier` | native `network_mode: service:pocketid` |
| Plugin DLL injection | `WithResourceMapping` | bind mount |
| Renovate | needs a `customManagers` regex on image constants (`// renovate: datasource=docker depName=jellyfin/jellyfin`) | native `docker-compose` manager |
| Local dev ergonomics | one command, debuggable, Playwright headed mode | two steps |

**Recommendation: Testcontainers.** Keep both image references in one constants file annotated for a Renovate regex `customManagers`, and put that manager in the same group as the Jellyfin NuGet packages, which meets the standing "one Renovate group" constraint. The "GitHub-hosted runners have the same configuration as Microsoft-hosted agents" note in the Testcontainers CI docs (<https://dotnet.testcontainers.org/cicd/>) plus the runner image manifest confirm Docker works with no setup.

## 6. GitHub Actions

- `runs-on: ubuntu-24.04` (or `ubuntu-latest`). Docker 28.0.4 and .NET SDK 10.0.x are preinstalled (`actions/runner-images` `images/ubuntu/Ubuntu2404-Readme.md`).
- Install the browser with Playwright's script after build: `pwsh <test-bin>/playwright.ps1 install --with-deps chromium`. Cache `~/.cache/ms-playwright` keyed on the Playwright version.
- Both images are multi-arch (amd64 and arm64), so `ubuntu-24.04-arm` also works if we ever want it.
- Expect roughly image pull + about 10–20 s Jellyfin startup per run. Use one fixture per test class/collection, not per test.

## 7. Fallback (only if the above fails)

Fallback order:

1. **Still PocketID, without passkeys.** Do the OIDC Login by signing in through the one-time access code (`/lc/<token>`) inside the authorize flow, with no WebAuthn involved. This keeps the real Identity Provider.
2. **navikt/mock-oauth2-server** (actively maintained, 6.1.4 released 2026-10-28). It is a spec-compliant mock that supports auth code + PKCE and configurable claims (`sub`, `groups`).

We don't need option 2 given the findings above.

## Spike checklist (verify first when implementing)

1. Netns sharing via `WithCreateParameterModifier` works with Testcontainers' port mapping and wait strategies for the *second* container.
2. `Credentials.InstallAsync()` satisfies PocketID's registration and login (`@simplewebauthn/browser` 13.x and PocketID's attestation/UV policy). If not, fall back to CDP `WebAuthn.addVirtualAuthenticator` with `isUserVerified: true` as PocketID does.
3. A credential captured with `GetAsync` and re-seeded into a fresh context is accepted for discoverable login (PocketID's `/login` uses `startAuthentication` without an allowList).
4. The Jellyfin `/Startup/*` sequence on 12.4 runs end to end without the web UI.

## Open questions surfaced

- **Real-world split-horizon issuer.** Production deployments behind reverse proxies or with internal Docker DNS hit the same "browser URL ≠ back-channel URL" problem, and PocketID ships `INTERNAL_APP_URL` for it. Should the Plugin support a separate internal metadata/back-channel URL, or rely only on the discovery document's endpoints (which `INTERNAL_APP_URL` already rewrites)? This belongs with the security hardening / callback-URL work.

## Sources

- pocket-id/pocket-id @ v2.19.1: `backend/internal/common/env_config.go`, `backend/internal/apikey/service.go`, `backend/internal/middleware/authenticator.go`, `backend/internal/middleware/rate_limit.go`, `backend/internal/controller/{e2etest,oidc,user,user_group,well_known}_controller.go`, `backend/internal/dto/{user,user_group,oidc}_dto.go`, `backend/internal/onetimeaccess/{handler,service}.go`, `backend/internal/webauthn/service.go`, `backend/internal/oidc/provider.go`, `frontend/src/routes/{lc,settings/account,login}`, `tests/utils/passkey.util.ts`
- PocketID env var docs: <https://pocket-id.org/docs/configuration/environment-variables>
- ory/fosite `authorize_helper.go` (`IsRedirectURISecure`, `IsLocalhost`)
- microsoft/playwright `docs/src/api/class-credentials.md`, `docs/src/api/class-cdpsession.md`, `packages/injected/src/webAuthn.ts`, PR #40849, issue #7276
- jellyfin/jellyfin @ v12.4: `Jellyfin.Api/Controllers/StartupController.cs`, `Jellyfin.Api/Models/StartupDtos/*`, `global.json`
- jellyfin/jellyfin-packaging `docker/Dockerfile`
- testcontainers/testcontainers-dotnet @ 4.16.0: `src/Testcontainers/Builders/*`. CI docs: <https://dotnet.testcontainers.org/cicd/>
- actions/runner-images `images/ubuntu/Ubuntu2404-Readme.md`
- dotnet/runtime PRs #128068, #130504 (`*.localhost` resolution)
- navikt/mock-oauth2-server releases
