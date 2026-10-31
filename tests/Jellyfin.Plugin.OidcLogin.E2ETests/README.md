# End-to-end tests

These tests run a real PocketID and a real Jellyfin (with the Plugin installed) in containers, and drive them with Playwright Chromium and its virtual WebAuthn authenticator. CI runs them in the `e2e` job.

## How the harness is wired

- **One environment per test run.** `E2EEnvironment` is an xUnit assembly fixture. It installs the Plugin as a release would (every artifact `build.yaml` lists), starts both containers, completes Jellyfin's first-run wizard as `admin`, configures the Plugin for PocketID, and seeds PocketID through its API with the `STATIC_API_KEY`. Seeding creates two groups, the confidential OIDC client `jellyfin` (PKCE enforced, consent skipped, group-restricted), and Alice with a fixed Subject. It then enrols a passkey for Alice through PocketID's UI.
- **Shared network namespace.** PocketID and Jellyfin join one network namespace, owned by a placeholder container (the PocketID image running `sleep`). WebAuthn needs a secure origin, so PocketID's `APP_URL` is `http://localhost:P`, and inside the namespace Jellyfin reaches the issuer at that same URL. The placeholder publishes `P:P` and Jellyfin's 8096 on a random port, so PocketID can be stopped while Jellyfin stays reachable.
- **Outage tests run last.** Tests that stop PocketID (`WithPocketIdStoppedAsync`) belong to the `IdentityProviderOutage` collection, which xUnit runs alone after all others. PocketID restarts with its data afterwards.
- **Two hostnames.** The browser uses `localhost` for PocketID and `127.0.0.1` for Jellyfin, so the two apps' cookies stay apart.
- **Fresh passkey contexts.** `NewSessionAsync(withAlicePasskey: true)` opens a fresh browser context that holds only Alice's captured passkey and has no PocketID session. Signing in from it is a real passkey assertion.
- **Pinned images.** Both image references live in `ContainerImages.cs`, digest-pinned, with `// renovate:` comments that Renovate's regex manager will match once it is configured (#34).

## Artifacts

Every browser session writes a Playwright trace to `traces/`, and every run writes `jellyfin.log` and `pocketid.log`. They go to `$E2E_ARTIFACTS_DIR`, which defaults to `e2e-artifacts/` next to the test binaries. Open a trace with `pwsh bin/Debug/net10.0/playwright.ps1 show-trace <file>` or at <https://trace.playwright.dev>. CI uploads all of them when the job fails.

## Running locally with podman (rootless)

```sh
systemctl --user start podman.socket
export DOCKER_HOST=unix://$XDG_RUNTIME_DIR/podman/podman.sock
export TESTCONTAINERS_RYUK_DISABLED=true
dotnet test --project tests/Jellyfin.Plugin.OidcLogin.E2ETests
```

- **Ryuk disabled.** Testcontainers' Ryuk reaper doesn't run under rootless podman. The fixture removes its containers itself, and `podman ps -a` shows any leftovers after an aborted run.
- **Chromium download.** The fixture installs Playwright Chromium on first use. On a distribution Playwright doesn't support (such as Fedora) it falls back to its Ubuntu build, which works when the usual desktop libraries are present.
- **`127.0.0.1` for Jellyfin.** Rootless podman's port forwarding (pasta) doesn't forward IPv6, so the harness reaches Jellyfin through `127.0.0.1`, not `localhost`. PocketID has to stay on `localhost`, because WebAuthn rejects IP addresses as relying-party IDs. Chromium falls back from `::1` to `127.0.0.1` for it.

With Docker, only the last command is needed.
