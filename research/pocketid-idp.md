# Research: PocketID as the Identity Provider

Ticket: [#5](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/5) · Researched 2026-10-29

**Question.** What does current PocketID expose for OIDC: discovery document, PKCE (and can it be required), signing algorithms, claims (`groups`, `preferred_username`, `email`), client registration options (public vs confidential, redirect URI rules), and which spec deviations must the Plugin tolerate?

## Sources (all primary)

- **PocketID source, latest release `v2.19.1`** (published 2026-10-22, tag commit `23c7f07f`), via `gh release list -R pocket-id/pocket-id`. Paths below are relative to `backend/internal/` at that tag.
- **`pocket-id/fosite` `v1.4.3`**: PocketID's fork of ory/fosite, pinned in `backend/go.mod`. PocketID's OIDC/OAuth engine is built on it.
- **pocket-id.org docs**: source repo `pocket-id/website` at commit `f0e165b7` (2026-10-28), under `src/content/docs/docs/`.
- **OpenID Connect Core 1.0 / Discovery 1.0, RFC 7636 (PKCE), RFC 9207 (`iss` in the authorization response), RFC 8252 §7.3 (loopback redirect).**

## 1. Discovery document

PocketID serves `GET {APP_URL}/.well-known/openid-configuration`. It serves the same JSON at `/.well-known/oauth-authorization-server` (RFC 8414). Source: `controller/well_known_controller.go` `computeServerMetadata()`.

| Field | Value at v2.19.1 |
| --- | --- |
| `issuer` | `APP_URL` (scheme + host, **no path allowed**, no trailing slash) |
| `authorization_endpoint` | `APP_URL/authorize` |
| `token_endpoint` | `INTERNAL_APP_URL/api/oidc/token` |
| `userinfo_endpoint` | `INTERNAL_APP_URL/api/oidc/userinfo` |
| `jwks_uri` | `INTERNAL_APP_URL/.well-known/jwks.json` |
| `end_session_endpoint` | `APP_URL/api/oidc/end-session` (out of scope for us) |
| `pushed_authorization_request_endpoint` | `INTERNAL_APP_URL/api/oidc/par` (`require_pushed_authorization_requests: false`) |
| `response_types_supported` | `["code"]` only |
| `response_modes_supported` | `query`, `fragment`, `form_post` |
| `grant_types_supported` | `authorization_code`, `refresh_token`, device code, `client_credentials` |
| `scopes_supported` | `openid profile email groups offline_access` |
| `claims_supported` | `sub given_name family_name name display_name email email_verified preferred_username picture groups auth_time amr` |
| `subject_types_supported` | `["public"]` |
| `id_token_signing_alg_values_supported` | exactly **one** value, the alg of the current signing key (default `RS256`) |
| `code_challenge_methods_supported` | `["plain","S256"]` |
| `token_endpoint_auth_methods_supported` | `client_secret_basic`, `client_secret_post`, `none` |
| `authorization_response_iss_parameter_supported` | `true` (RFC 9207) |
| `prompt_values_supported` | `none login consent select_account` |
| `request_parameter_supported` | `true`, but only unsigned (`request_object_signing_alg_values_supported: ["none"]`) |
| `backchannel_logout_supported` | `true` |

`INTERNAL_APP_URL` defaults to `APP_URL` (`common/env_config.go` ~L402). When an operator sets it, the back-channel endpoints in discovery point at the internal host while `issuer` and `authorization_endpoint` stay on the public host. Docs: `configuration/environment-variables.md`, `setup/connect-an-app.mdx` ("The token, userinfo, introspection, JWKS and pushed authorization endpoints use `INTERNAL_APP_URL`…").

**Implication for the Plugin:** use discovery. Never derive the token, userinfo or JWKS URLs from the issuer. Validate the ID token's `iss` against discovery's `issuer`, not against the host the Plugin fetched from.

## 2. PKCE

- **Supported methods:** `S256` and `plain`. fosite is configured with `EnablePKCEPlainChallengeMethod: true` and `EnforcePKCEForPublicClients: true` (`oidc/provider.go` L53–54). The Plugin should always send `S256`.
- **Can it be required? Yes, per client.**
  - Public clients always require PKCE: `client.PkceEnabled = input.IsPublic || input.PkceEnabled` (`service/oidc_service.go` ~L308).
  - Confidential clients have an admin toggle (`PkceEnabled`). When it is on, an authorize request without `code_challenge` is rejected with `invalid_request` "This client requires PKCE…" (`oidc/authorization_service.go` `validateClientPKCERequirement`, ~L809).
  - When the toggle is off and a client sends `code_challenge` anyway, PocketID flags the client `PkceSupported` (~L185) and prompts the admin in the UI to turn on enforcement. Even with the toggle off, fosite's PKCE handler still checks the verifier once a challenge was sent.
- PKCE cannot be forced to `S256`-only server-wide, because `plain` is enabled globally. Downgrade protection is the Plugin's job: it always generates `S256`.

## 3. Signing algorithms and keys

- **Default:** a generated RSA-2048 key with `RS256` (`service/jwt_service.go` `generateKey()` ~L211).
- **Alternatives:** the `key-rotate` CLI (`cmds/key_rotate.go` L54) supports `--alg RS256|RS384|RS512|ES256|ES384|ES512|EdDSA`, with `--crv Ed25519` for EdDSA. Docs: `guides/scopes-and-claims.md` "Tokens are signed with RS256 by default, and Custom signing keys switches to another algorithm".
- **One key at a time:** discovery advertises one alg. The ID token header carries `alg` and `kid` (`oidc/signer.go` L33). `key-rotate` **replaces** the key, so the JWKS holds only the new one with no overlap window.
- **Implication:** the Plugin must accept at least RS256 and should also accept ES256 and EdDSA. On an unknown `kid` it must re-fetch the JWKS rather than fail permanently (OIDC Core §10.1.1). A rotation invalidates in-flight ID tokens, which is harmless for us because ID tokens are consumed immediately.

## 4. Claims

All of this comes from `oidc/claims_service.go` `GetUserClaims()` and docs `guides/scopes-and-claims.md`. The same claims go into **both the ID token and the userinfo response**, so the Plugin does not need userinfo.

| Scope | Claims | Notes |
| --- | --- | --- |
| `openid` | `sub` | `sub` = PocketID user ID, a **UUIDv4 string** (`model/base.go` `BeforeCreate`). Docs say it "never changes". This is the Subject the Linked Identity is keyed by. |
| `profile` | `given_name`, `family_name`, `name`, `display_name` (non-standard), `preferred_username`, `picture` (always set, a PocketID URL), plus **custom claims** | `preferred_username` = PocketID username, regex `^[a-zA-Z0-9]([a-zA-Z0-9_.@-]*[a-zA-Z0-9])?$`, 1–50 chars (`dto/validations.go` L25, `dto/user_dto.go`). |
| `email` | `email`, `email_verified` (bool) | **Omitted entirely** if the user has no email. Email is optional unless the admin sets `REQUIRE_USER_EMAIL` (which defaults to true). `email_verified` starts as false unless `EMAILS_VERIFIED=true`. |
| `groups` | `groups`: **JSON array of strings** | Values are each group's **`Name`** (2–255 chars), **not** the `FriendlyName` shown prominently in the admin UI. The array is empty when the user has no groups. The claim is only present if the `groups` scope is requested. |
| `offline_access` | none | PocketID issues refresh tokens without it. We don't need refresh tokens. |

Other claims in the ID token:

- `iss`, `aud`, `exp`, `iat`, `auth_time`, `nonce` (if sent), `at_hash` (fosite `handler/openid/flow_explicit_token.go`).
- `amr`, with value `["phr"]` for a passkey or `["otp"]` for a one-time access link or login code (`service/jwt_service.go` L36–39).
- A non-standard `"type": "id-token"` (`common/claims.go`).
- `aud` is a JSON **array** containing the client ID (fosite `strategy_jwt.go` L229). OIDC Core allows array or string, so the parser must accept both.
- ID token lifetime is 1 hour.

**Custom claims:** set per user or per group by admins and released under `profile`. JSON-valued strings are emitted as JSON. Reserved names (`sub`, `email`, `groups`, `preferred_username`, `name`, `type`, `iss`, `aud`, `exp`, `iat`, `auth_time`, `nonce`, …) are rejected (`service/custom_claim_service.go` `isReservedClaim`). So the Plugin can trust that `groups` and `preferred_username` were never overridden by a custom claim.

**`preferred_username` is user-mutable by default.** `ALLOW_OWN_ACCOUNT_EDIT` defaults to `true` (`appconfig/model.go` L143). When it is on, users can change their **own username**, plus names and email (`service/user_service.go` `canEditProfile` and the update block ~L650–656). Group membership and admin status are admin-only. OIDC Core §5.7 likewise says `preferred_username` MUST NOT be relied on as unique or stable.

## 5. Client registration

Clients are created by an admin under **Administration → OIDC Clients** (docs `setup/connect-an-app.mdx`). There is no dynamic client registration endpoint for us; CIMD exists but is only for allow-listed URL client IDs.

- **Client types:**
  - **Confidential.** A secret is generated on create and shown once. Multiple active secrets are allowed, for rotation. Token endpoint auth is `client_secret_basic` or `client_secret_post`. Federated JWT client assertions are also possible but irrelevant here.
  - **Public.** No secret (`token_endpoint_auth_method=none`), and PKCE is forced.
- **Per-client options** (`service/oidc_service.go` `updateOIDCClientModelFromDto`):
  - Callback URLs
  - Logout callback URLs
  - Back-channel logout URL
  - PKCE toggle
  - Requires re-authentication
  - Requires PAR
  - Skip consent screen
  - Launch URL
  - Group restriction ("Allowed user groups")
  - Access and refresh token lifetimes
- **Access is deny-by-default:** a new client admits nobody until the admin picks allowed groups or "All Users". Otherwise the user gets `access_denied` "You are not allowed to access this service" (docs `setup/connect-an-app.mdx`; `IsUserGroupAllowedToAuthorize` in `oidc/authorization_service.go`).
- **Consent screen:** shown on the first authorization, and again when more scopes are requested. It can be disabled per client ("Skip Consent Screen"), or forced by `prompt=consent`.
- **Redirect URI rules** (docs `advanced/callback-url-wildcards.md`; `oidc/provider.go` `matchRedirectURI`, `utils/callback_url_util.go`):
  - Exact match, unless the registered URL contains wildcards. `*` matches within one segment and `**` matches across path segments.
  - **At least one callback URL is mandatory.** PocketID no longer auto-saves the first URL a client sends.
  - With exactly one registered callback URL, `redirect_uri` may be omitted. The Plugin should always send it anyway.
  - Loopback `http://localhost` and `http://127.0.0.1` registered without a port match any port (RFC 8252 §7.3).
  - `http://` on non-loopback hosts is allowed while `ALLOW_INSECURE_CALLBACK_URLS=true`, which is **the current default ("for now")**. The docs advise setting it to `false`.
  - The `javascript:` and `data:` schemes are rejected.

## 6. Deviations and quirks the Plugin must tolerate

1. **`state` is mandatory and must be ≥ 8 characters.** A non-empty `nonce` must also be ≥ 8 characters. fosite enforces `MinParameterEntropy = 8` (`pocket-id/fosite` `authorize_request_handler.go` ~L583, `handler/openid/strategy_jwt.go` L222). OIDC Core leaves `state` optional. The Plugin must always send a high-entropy `state`, and should send a `nonce` too.
2. **Split hosts via `INTERNAL_APP_URL`.** The token, userinfo and JWKS endpoints may sit on a different host from `issuer`. Use discovery and never assume the same origin (§1).
3. **The only signing alg is the operator's choice.** Discovery lists one. Support RS256, ES256 and EdDSA, and re-fetch the JWKS on an unknown `kid` (§3).
4. **`aud` is an array.** Validate that it contains the client ID. There's no `azp`, which is fine with a single audience.
5. **Non-standard extras:** `display_name` and `type` claims. Ignore them.
6. **`plain` PKCE is advertised.** Always use `S256`.
7. **`email` may be absent.** `email_verified` may be false. `preferred_username` is user-editable by default.
8. **`amr` can be `otp`, not only `phr`.** One-time access links and login codes are a non-passkey sign-in path, so "passkey-only" is a deployment property of PocketID, not something the token guarantees. The Plugin speaks plain OIDC (a settled constraint), so this needs no handling. It is still worth knowing, and the Plugin *could* optionally require `amr` to contain `phr`.
9. **RFC 9207 `iss` is returned on the authorization response.** The Plugin may verify it against the issuer, as cheap mix-up defense.
10. **ID token lifetime is 1 h** and the authorization code lifetime is 15 min (docs `guides/scopes-and-claims.md`). Neither constrains us.
11. **Group-restricted clients are enforced by PocketID** (`access_denied` at authorize). The Plugin's Failure UX must handle an `error=access_denied` callback.

## Recommendation for the spec

- Register the Plugin as a **confidential client** with the **PKCE toggle on**. Jellyfin has a server side that can keep the secret, and this gives us both secret auth and enforced PKCE.
  - Authenticate with `client_secret_basic`.
  - Send `S256`, a `state` of ≥ 8 characters, and a `nonce`.
  - Use `response_type=code` with `response_mode=query`.
- Request scopes `openid profile email groups`. Read all claims from the **ID token**; userinfo is unnecessary.
- **Linked Identity** key = `iss` + `sub`. `sub` is a stable UUID.
- **Admin Group** matching is against the `groups` array of group **`Name`s**. Plugin docs should tell admins to use the group's *Name*, not its *Friendly Name*.
- **Username Adoption** must be treated as dangerous with PocketID. By default (`ALLOW_OWN_ACCOUNT_EDIT=true`) any PocketID user can rename themselves to an existing Jellyfin username, such as an admin's, and adopt that account. This backs the settled "opt-in, off by default" decision. The Plugin's config docs should say that enabling it requires `ALLOW_OWN_ACCOUNT_EDIT=false` on PocketID.
- Use `preferred_username` only as the *initial* Jellyfin username under Just-in-time Provisioning, never for lookup after linking.
- **Callback URL:** exactly one, registered exact (no wildcards). The Plugin must show admins the exact URL to paste into PocketID.
- **Integration tests:** PocketID's defaults (`ALLOW_INSECURE_CALLBACK_URLS=true`, `INTERNAL_APP_URL` support) let a Docker network use `http://` callback URLs and an internal hostname for back-channel calls while the browser uses the public one. This is a convenient test of the split-host case.

## Open questions surfaced

- **Username Adoption safety.** Should the spec make Username Adoption conditional on something stronger than a toggle? For example, warn in the config UI, or only adopt when the Jellyfin user has no password set. Or should it just document the `ALLOW_OWN_ACCOUNT_EDIT=false` prerequisite? (Fits the "Plugin configuration page" and "Security hardening review" items.)
- **Seeding PocketID in integration tests.** Seeding needs an admin-created client with callback URL, allowed groups and PKCE toggle, plus a user who has a passkey. Does PocketID's API or its `e2etest` controller (`controller/e2etest_controller.go`, test builds only) make this scriptable, or must the harness drive the admin UI?
