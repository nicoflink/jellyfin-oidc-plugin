# OIDC client implementation options inside a Jellyfin Plugin

Research for [#4](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/4). Researched 2026-10-29.

**Question:** Which approach should the Plugin use for Authorization Code + PKCE, discovery, JWKS and ID token validation against the Identity Provider?

1. `Microsoft.IdentityModel.Protocols.OpenIdConnect` + `System.IdentityModel.Tokens.Jwt` / `Microsoft.IdentityModel.JsonWebTokens`
2. `Duende.IdentityModel.OidcClient`
3. Hand-rolled

The answer has to account for how Jellyfin loads plugin assemblies, licensing, and how well each option is maintained.

## Recommendation

**Use Microsoft.IdentityModel for the cryptography and hand-roll the thin flow layer.**

- **Microsoft.IdentityModel handles the security-critical parts:**
  - `ConfigurationManager<OpenIdConnectConfiguration>` fetches and caches discovery and JWKS, and refreshes on key rotation.
  - `JsonWebTokenHandler` validates the ID token: signature, `iss`, `aud`, `exp`/`nbf`.
- **The Plugin's own code handles the protocol plumbing,** about 100 lines using only the BCL:
  - building the authorize URL
  - PKCE (S256)
  - `state`/`nonce` generation and storage
  - the token-endpoint POST via `IHttpClientFactory`
  - checking `nonce`
- **Don't adopt Duende OidcClient.** It brings little, and its defaults skip ID token signature validation.

## Findings

### 1. Target platform: latest stable Jellyfin is 12.4 on .NET 10

- Jellyfin's latest stable release is **v12.4**, released 2026-10-26. Versioning moved from 10.x to 12.x. ([releases](https://github.com/jellyfin/jellyfin/releases/tag/v12.4))
- It builds with SDK 10.0 ([`global.json@v12.4`](https://github.com/jellyfin/jellyfin/blob/v12.4/global.json)).
- `MediaBrowser.Controller` targets `net10.0` at `VersionPrefix` 12.4.0 ([csproj@v12.4](https://github.com/jellyfin/jellyfin/blob/v12.4/MediaBrowser.Controller/MediaBrowser.Controller.csproj)).
- **The Plugin targets `net10.0`.**

### 2. Jellyfin ships no Microsoft.IdentityModel assemblies

- **Source check:** [`Directory.Packages.props@v12.4`](https://github.com/jellyfin/jellyfin/blob/v12.4/Directory.Packages.props) references none of these packages:
  - `Microsoft.IdentityModel.*`
  - `System.IdentityModel.Tokens.Jwt`
  - `Duende.*`
  - any `Microsoft.AspNetCore.Authentication.*` packages
- **Release check:** the 12.4 release tarball (`jellyfin_12.4-amd64.tar.gz` from repo.jellyfin.org) is self-contained, 2843 entries. Grepping it for `identity|jwt|duende|oidc|openid|tokens` finds only ASP.NET Core Identity:
  - `Microsoft.AspNetCore.Identity.dll`
  - `Microsoft.Extensions.Identity.Core.dll`
  - `Microsoft.Extensions.Identity.Stores.dll`
- **No JWT/OIDC assembly is loaded by the host.** So whichever library the Plugin ships, its version can't clash with a host copy.
- **Assemblies the host does ship,** which the Plugin must not ship itself (see §3):
  - `Microsoft.Extensions.Logging.Abstractions.dll`
  - `Microsoft.Extensions.Http.dll`
  - `System.Text.Json.dll`
  - and the rest of the ASP.NET Core shared framework

### 3. How Jellyfin loads plugin assemblies

Sources: [`PluginManager.cs@v12.4`](https://github.com/jellyfin/jellyfin/blob/v12.4/Emby.Server.Implementations/Plugins/PluginManager.cs) and [`PluginLoadContext.cs@v12.4`](https://github.com/jellyfin/jellyfin/blob/v12.4/Emby.Server.Implementations/Plugins/PluginLoadContext.cs).

- **One collectible `AssemblyLoadContext` per plugin.** It is created as `new PluginLoadContext(plugin.Path)` with `base(true)`. This came in with [PR #9115](https://github.com/jellyfin/jellyfin/pull/9115) (2023), which replaced one context per DLL.
  - So two plugins can ship different versions of the same third-party DLL. For example, an old SSO plugin carrying Duende 6.x would not conflict with this Plugin.
- **Which DLLs get loaded:**
  - Every `*.dll` in the plugin directory, recursively, goes into that context through `LoadFromAssemblyPath`.
  - If `meta.json` has an `assemblies` list (the whitelist), only those files are loaded. Every whitelisted path must exist inside the plugin directory (`TryGetPluginDlls`).
  - **So every third-party dependency DLL must be in the release zip,** and in the whitelist if one is used.
- **Fallback to the host's copy.** `PluginLoadContext.Load` resolves through an `AssemblyDependencyResolver`. When that returns `null`, the runtime falls back to the default context, which is Jellyfin's own copies.
  - This is how types shared with the host (`ILogger`, `IHttpClientFactory`, Jellyfin's own types) keep a single identity.
  - **Watch-out:** if the Plugin bundled its own `Microsoft.Extensions.*.dll` or `System.Text.Json.dll`, those would load into the plugin context. Their types would then not match the host's, and dependency injection would break.
  - So **ship only assemblies the host doesn't have**, and compile against shared-framework versions no newer than Jellyfin's. Jellyfin 12.4 uses 10.0.14 for `Microsoft.Extensions.*`, and their assembly version is 10.0.0.0.
- **Failure modes:**
  - After loading, `assembly.GetTypes()` is called. A `TypeLoadException` or `ReflectionTypeLoadException` sets the plugin to `NotSupported`, with the log line *"plugin references an incompatible version of one of the shared libraries"*.
  - Any other load failure sets it to `Malfunctioned`.
- **Precedent:** 9p4's SSO plugin already handles this by listing exactly the DLLs to package in its `build.yaml` `artifacts` ([build.yaml](https://github.com/9p4/jellyfin-plugin-sso/blob/main/build.yaml)):
  - `SSO-Auth.dll`
  - `Duende.IdentityModel.OidcClient.dll`
  - `Duende.IdentityModel.dll`

### 4. Option 1: Microsoft.IdentityModel (AzureAD/identitymodel-extensions)

- **Current version: 8.23.0**, published 2026-09-18, MIT licence. No deprecation and no known vulnerabilities on NuGet. ([nuget](https://www.nuget.org/packages/Microsoft.IdentityModel.Protocols.OpenIdConnect/8.23.0))
- **Maintenance:** the repo is active, last push 2026-10-29 ([repo](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet)).
- **What it provides:**
  - discovery and JWKS retrieval with caching, via `ConfigurationManager<OpenIdConnectConfiguration>`
  - signature and claim validation
  - an `OpenIdConnectProtocolValidator` for `nonce`, `at_hash` and `c_hash`
- **What it doesn't provide:** it is not a flow client. Building the authorize request, PKCE and the code redemption are up to the caller.
- **DLLs to ship (net10.0 closure, from the NuGet dependency groups):**
  - `Microsoft.IdentityModel.Protocols.OpenIdConnect`
  - `Microsoft.IdentityModel.Protocols`
  - `System.IdentityModel.Tokens.Jwt` (a hard dependency of Protocols.OpenIdConnect)
  - `Microsoft.IdentityModel.JsonWebTokens`
  - `Microsoft.IdentityModel.Tokens`
  - `Microsoft.IdentityModel.Logging`
  - `Microsoft.IdentityModel.Abstractions`
  - `Microsoft.Bcl.Cryptography`. On net10.0 this is only a 19 KB type-forwarding shim, from `lib/net10.0` in the 10.0.2 nupkg.
  - `Microsoft.Extensions.Logging.Abstractions` (`[8.0.0, )`) is supplied by the host.
- **Limitation: no EdDSA.** It can't verify EdDSA signatures. The issue [#2426](https://github.com/AzureAD/azure-activedirectory-identitymodel-extensions-for-dotnet/issues/2426) and draft PR #2427 are both still open. See §7.

### 5. Option 2: Duende.IdentityModel.OidcClient

- **Current version: 7.1.1**, published 2026-10-16. Apache-2.0, a free/OSS package from the [DuendeSoftware/foss](https://github.com/DuendeSoftware/foss) repo, which is active (last push 2026-10-21).
- **Dependencies:** it pulls in `Duende.IdentityModel` 8.1.0, which has no dependencies on net10.0, plus `Microsoft.Extensions.Logging.Abstractions >= 10.0.13`, supplied by the host. ([nuget](https://www.nuget.org/packages/Duende.IdentityModel.OidcClient/7.1.1))
- **Designed for native apps.** Its README positions it for *native* clients under RFC 8252: MAUI, WPF, console apps ([README](https://github.com/DuendeSoftware/foss/blob/main/identity-model-oidc-client/README.md)). It works server-side, but the caller has to persist `AuthorizeState` between the redirect and the callback.
- **ID token signature validation is off by default.**
  - `Policy.RequireIdentityTokenSignature` defaults to `false` ([Policy.cs](https://github.com/DuendeSoftware/foss/blob/main/identity-model-oidc-client/src/IdentityModel.OidcClient/Policy.cs)).
  - With no `IIdentityTokenValidator` configured, `ResponseProcessor` falls back to `NoValidationIdentityTokenValidator`, which just base64-decodes the payload into claims ([ResponseProcessor.cs](https://github.com/DuendeSoftware/foss/blob/main/identity-model-oidc-client/src/IdentityModel.OidcClient/ResponseProcessor.cs), [NoValidationIdentityTokenValidator.cs](https://github.com/DuendeSoftware/foss/blob/main/identity-model-oidc-client/src/IdentityModel.OidcClient/NoValidationIdentityTokenValidator.cs)).
  - On that path nothing checks `iss`, `aud`, `exp` or `nonce`.
- **The old validator package is deprecated.** `IdentityModel.OidcClient.IdentityTokenValidator` 6.0.0 is marked *Legacy*: *"With the removal of Hybrid Flow support … it is not necessary anymore to validate id_tokens. However, you can still do your own validation via our extensibility points."*
  - `Duende.IdentityModel.OidcClient.Extensions` 7.1.1 now contains only DPoP support, though it still depends on `System.IdentityModel.Tokens.Jwt [8.19.2, 9.0.0)`.
  - **So getting real ID token validation means writing an `IIdentityTokenValidator` on top of Microsoft.IdentityModel anyway** (Option 1's DLLs plus the 2 Duende DLLs).
- **What it does provide:**
  - PKCE
  - `state`
  - discovery-document policy checks (issuer name, endpoints on the same authority, HTTPS)
  - PAR
  - token redemption
  - userinfo loading

### 6. Option 3: fully hand-rolled

- **.NET 10's BCL covers the plumbing:**
  - `HttpClient` / `IHttpClientFactory`
  - `System.Text.Json`
  - `System.Buffers.Text.Base64Url` (Duende's own code uses it)
  - `RSA`/`ECDsa`, enough for RS*/ES* verification
  - `SHA256`, for PKCE
- **What would have to be written by hand:**
  - JWK-to-key import
  - selecting keys by `kid`
  - refreshing JWKS on rotation
  - restricting algorithms (no `none`, no confusion between HS and RS)
  - clock-skew handling
  - claim validation
- **This is the security-sensitive part,** and a maintained library already covers it.
- **Licensing and assembly loading:** no third-party DLLs at all, but no other advantage.

### 7. Identity Provider fact that constrains every option: PocketID key algorithms

- PocketID v2.19.1 (latest, 2026-10-22) signs with **RS256 by default**.
- Its `key-rotate` command also supports RS384/512, ES256/384/512 and **EdDSA (Ed25519)** ([key_rotate.go@v2.19.1](https://github.com/pocket-id/pocket-id/blob/v2.19.1/backend/internal/cmds/key_rotate.go)).
- Neither Microsoft.IdentityModel (#2426) nor the .NET BCL verifies Ed25519.
- The OIDC Core spec does permit relying on the TLS server validation of the back-channel token response instead of checking the ID token signature. This applies only to the code flow with the token received directly from the token endpoint ([OIDC Core §3.1.3.7, item 6](https://openid.net/specs/openid-connect-core-1_0.html#IDTokenValidation)).
- That is exactly the reasoning behind Duende's deprecation, and it is a defensible fallback, but `iss`/`aud`/`exp`/`nonce` must still be checked.

### 8. 9p4/jellyfin-plugin-sso (reference only)

- **Library:** `Duende.IdentityModel.OidcClient` 6.0.1, targeting net9.0 / Jellyfin 10.11 ([csproj](https://github.com/9p4/jellyfin-plugin-sso/blob/main/SSO-Auth/SSO-Auth.csproj)).
- **Validation:** it sets no `IIdentityTokenValidator` and never enables `RequireIdentityTokenSignature`, so its ID tokens go unverified ([SSOController.cs](https://github.com/9p4/jellyfin-plugin-sso/blob/main/SSO-Auth/Api/SSOController.cs) lines 124–149). It does expose admin toggles that weaken the discovery policy (`DoNotValidateEndpoints`, `DoNotValidateIssuerName`, `DisableHttps`).
- **Status: the repo is now archived.** README: *"Project archived because I'm tired of working on this after all the years."* The last release, v4.0.0.4 (2026-03-20), was a SAML security fix, and there is no Jellyfin 12.x release.

## Comparison

| | MS IdentityModel + thin flow | Duende OidcClient | Hand-rolled |
|---|---|---|---|
| ID token signature, iss/aud/exp | yes (`JsonWebTokenHandler`) | **off by default**, needs a custom validator built on the MS stack | must be written by hand |
| Discovery + JWKS caching/rotation | yes (`ConfigurationManager`) | discovery yes; JWKS only used by a validator you supply | must be written by hand |
| PKCE / state / nonce / code redemption | ~100 lines in the Plugin | yes | ~100 lines |
| DLLs shipped | 8 | 2 (unsafe defaults) or 10 (with validation) | 0 |
| Licence | MIT | Apache-2.0 | n/a |
| Maintenance | Microsoft, roughly monthly releases | Duende foss, active | the Plugin |
| EdDSA (PocketID optional) | no | no (no validator) | no (BCL lacks Ed25519) |

## Implications for the spec

- **Release packaging:**
  - Ship exactly the 8 IdentityModel DLLs next to the Plugin DLL.
  - Never ship `Microsoft.Extensions.*`, `System.Text.Json`, or Jellyfin's own assemblies.
  - Set `meta.json`/`build.yaml` artifact lists to match. This ties into the "Repo & project layout" item.
- **Renovate:** Microsoft.IdentityModel can be bumped on its own schedule, separately from the Jellyfin group, because the host never loads it.
- **Signing algorithms:** decide whether the Plugin supports only RS*/ES*-signed ID tokens (PocketID's default) or also accepts EdDSA by relying on TLS per OIDC Core §3.1.3.7. This is an open question for the security hardening item.
