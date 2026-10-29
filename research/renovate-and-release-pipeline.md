# Research: Renovate and release pipeline for a Jellyfin Plugin

Ticket: [#7](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/7). Researched 2026-10-29.
Terms follow `GLOSSARY.md` (the Plugin, Identity Provider, ...).

## Question

How do we configure Renovate so that **one PR** bumps the `jellyfin/jellyfin` Docker image tag used by tests
together with the Jellyfin NuGet packages (`Jellyfin.Controller`, `Jellyfin.Model`) and the Plugin's `targetAbi`?
What's the plugin repository `manifest.json` format? Should we use jprm or a custom GitHub Actions release, how do we
publish via GitHub Pages, and how do other plugins handle this?

## TL;DR

1. **Jellyfin changed its version scheme.** After 10.11.x came **12.0** (2026-09-08). The current stable release is **12.4**
   (2026-10-26). NuGet `Jellyfin.Controller`/`Jellyfin.Model` are `12.4.0`. Docker Hub tags are `12.4`, `12`, `latest`,
   and `12.4.<yyyymmdd-hhmmss>`. The server targets **.NET 10** (`net10.0`). The official plugin template is still on
   10.11.5/net9.0, so don't copy its versions.
2. **Renovate can bump everything in one PR.** Use a single `packageRules` entry with
   `groupName: "Jellyfin"` that matches the NuGet packages (nuget manager) and the Docker image (a regex `customManagers`
   entry on the test file, since a C# Testcontainers string isn't parsed natively). Add a second regex
   custom manager for `targetAbi` in `build.yaml`, using `datasource=nuget`/`depName=Jellyfin.Controller`, so it moves
   in lockstep. Set `minimumGroupSize` so the PR only opens once the Docker image **and** the NuGet packages exist.
3. **`targetAbi` sets a minimum server version, not an exact match** (`ApplicationVersion >= targetAbi`). Official plugins
   keep `targetAbi: "12.0.0.0"` for the whole 12.x line and bump it only on a major. We support only the latest
   stable release, so we should bump it to `12.<minor>.0.0` together with the packages.
4. **Release: build with jprm and host it ourselves on GitHub.** Build with the jprm GitHub Action. Attach the zip and `.md5`
   to the GitHub Release. Then run `jprm repo add --plugin-url <release-asset-url>` against a `manifest.json` on a
   `gh-pages` branch, which is served at `https://nicoflink.github.io/jellyfin-oidc-plugin/manifest.json`.
   We can't call the shared `jellyfin-meta-plugins` publish workflow because it pushes over SSH to repo.jellyfin.org.

## 1. Current Jellyfin versions (verified 2026-10-29)

| Thing | Value | Source |
|---|---|---|
| Latest stable server release | `v12.4` (2026-10-26), before that 12.3, 12.2, 12.1, 12.0 (2026-09-08) | `gh release list -R jellyfin/jellyfin` |
| NuGet `Jellyfin.Controller` | `12.4.0` (published 2026-10-26T21:11Z) | `api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json`, registration index |
| NuGet `Jellyfin.Model` | `12.4.0` | `api.nuget.org/v3-flatcontainer/jellyfin.model/index.json` |
| Docker Hub `jellyfin/jellyfin` | `12.4` / `12` / `latest` / `12.4.20261026-211555` (+ `-amd64`/`-arm64` variants) pushed 2026-10-26T21:26Z; `unstable` + `YYYYMMDDHH` nightlies | `hub.docker.com/v2/repositories/jellyfin/jellyfin/tags` |
| Target framework | `net10.0` | Jellyfin 12.0 release post; `jellyfin-meta-plugins` build workflow default `dotnet-target: net10.0`; official plugin `build.yaml`s |

**The scheme change.** The [12.0 release post](https://jellyfin.org/posts/jellyfin-release-12.0/) says: *"we are dropping the major
version "10" from our naming scheme … What would have been 10.12.0 is simply 12.0, and the server reports its version as `12.0.0`."*
Plugins built for 10.11 don't load on 12.0. Official plugins treat 12.x as one ABI line: `jellyfin-plugin-ldapauth` and
`jellyfin-plugin-opensubtitles` both ship `targetAbi: "12.0.0.0"` and `framework: "net10.0"`. Their "Unstable" preview PRs
move to `Jellyfin.Controller 13.*-*` / `targetAbi: "13.0.0.0"` (ldapauth PR #234).

**The version strings differ by source:**
- Docker uses `MAJOR.MINOR` (`12.4`). There's no `12.4.0` tag.
- NuGet uses `MAJOR.MINOR.PATCH` (`12.4.0`).
- `targetAbi` / the server's `System.Version` uses four parts (`12.4.0.0`).
- Releases land on NuGet about 15 minutes to 2 hours before the Docker tag (12.4: NuGet 21:11Z, Docker 21:26Z; 12.3: NuGet
  10-22 23:31Z, Docker 10-23 01:50Z).

**Stale template.** [`jellyfin/jellyfin-plugin-template`](https://github.com/jellyfin/jellyfin-plugin-template) still pins
`Jellyfin.Controller`/`Jellyfin.Model` `10.11.5`, `targetAbi: "10.11.0.0"`, `net9.0`. Its last version bump was 2026-09-03.
Use it for structure only, not for versions. One thing worth copying: it references the Jellyfin packages with
`<ExcludeAssets>runtime</ExcludeAssets>`, so the server's own assemblies aren't shipped in the plugin zip.

## 2. Renovate: one group for image + NuGet + targetAbi

### What the docs guarantee

- `groupName` is free text, and *"All updates sharing the same `groupName` will be placed into the same branch/PR"*. It isn't
  limited to one manager or datasource. (`docs/usage/configuration-options.md`, `## groupName`, Renovate 44.196.0.)
- `minimumGroupSize`: *"branch creation will be postponed until x or more updates are available in the branch"*. Its own
  example is exactly our case ("We need to update Node in two places - always wait until both upgrades are available").
- Custom regex managers: `customType: "regex"`, `managerFilePatterns` (renamed from `fileMatch`, which is auto-migrated),
  `matchStrings` with named groups `depName`/`packageName`, `currentValue`/`currentDigest`, `datasource`, plus
  `*Template` fallbacks (`datasourceTemplate`, `depNameTemplate`, `versioningTemplate`, ...). Uses RE2, so no lookahead.
  ([regex manager docs](https://docs.renovatebot.com/modules/manager/regex/))
- The NuGet manager's default `managerFilePatterns` cover `*.csproj` and `*.props`/`*.targets`, so `Directory.Packages.props`
  or `Directory.Build.props` work. It also covers `global.json` (`dotnet-sdk`).
  ([nuget manager docs](https://docs.renovatebot.com/modules/manager/nuget/))
- Replacement updates are never grouped. Major updates get their own branch by default (`separateMajorMinor`), so a
  12.x→13.0 jump arrives as a separate "major" Jellyfin group PR. We want that, because a major means rebuilding against a
  new ABI.

### What official plugins do

`jellyfin/jellyfin-plugin-template/.github/renovate.json` only extends `github>jellyfin/.github//renovate-presets/plugin`, which:
- groups `Jellyfin.*` (`groupName: "Jellyfin"`, `groupSlug: "jellyfin-{{newVersion}}"`)
- disables `Microsoft.*`/`System.*` NuGet updates
- pins `jellyfin/jellyfin-meta-plugins` workflows to `master`
- extends a `dotnet` preset, then a `default` preset (`config:base`, dashboard disabled, `docker:enableMajor`,
  `helpers:pinGitHubActionDigests`, ...)

Official plugins **don't** use Renovate for `targetAbi`. That bump comes from manual or bot "Update to latest Jellyfin
packages" PRs, and their csproj uses floating versions such as `Version="12.*-*"`. None of them run Jellyfin in a Docker
container in tests, so they have no image to group.

### Recommended `renovate.json`

Write our own config. Extending the Jellyfin preset would bring in their meta-plugins rule, the deprecated `config:base`,
and a disabled dashboard, which we don't want. We pin exact versions (`12.4.0`), not floating ones, so the Plugin always
builds against the version that's tested.

```json5
{
  "$schema": "https://docs.renovatebot.com/renovate-schema.json",
  "extends": ["config:recommended", "helpers:pinGitHubActionDigests"],
  "customManagers": [
    {
      "description": "Jellyfin Docker image used by integration tests (Testcontainers string)",
      "customType": "regex",
      "managerFilePatterns": ["/^tests/.+\\.cs$/"],
      "matchStrings": [
        "// renovate: datasource=(?<datasource>\\S+) depName=(?<depName>\\S+)\\s+.*?\"[^\":]+:(?<currentValue>[^\"@]+)(@(?<currentDigest>sha256:[a-f0-9]+))?\""
      ]
    },
    {
      "description": "targetAbi in build.yaml follows Jellyfin.Controller (12.4.0 -> \"12.4.0.0\")",
      "customType": "regex",
      "managerFilePatterns": ["/(^|/)build\\.yaml$/"],
      "matchStrings": ["targetAbi:\\s*\"(?<currentValue>\\d+\\.\\d+\\.\\d+)\\.0\""],
      "datasourceTemplate": "nuget",
      "depNameTemplate": "Jellyfin.Controller"
    }
  ],
  "packageRules": [
    {
      "description": "Disable Microsoft/System NuGet bumps (pulled in by Jellyfin, same as the official preset)",
      "matchDatasources": ["nuget"],
      "matchPackageNames": ["/^Microsoft\\./", "/^System\\./"],
      "enabled": false
    },
    {
      "description": "Jellyfin server image + NuGet packages + targetAbi move together",
      "matchPackageNames": ["Jellyfin.Controller", "Jellyfin.Model", "jellyfin/jellyfin"],
      "groupName": "Jellyfin",
      "minimumGroupSize": 4
    }
  ]
}
```

The test file would contain something like:

```csharp
// renovate: datasource=docker depName=jellyfin/jellyfin
private const string JellyfinImage = "jellyfin/jellyfin:12.4@sha256:…";
```

Notes and pitfalls:
- **Docker tag selection.** Renovate's default `docker` versioning only offers candidates with the same number of
  version parts and the same suffix. From `12.4` it moves to `12.5`/`13.0` and skips `12.4.20261026-211555`,
  `2026102610`, `unstable`, `latest`. Pinning the digest makes rebuilt images under the same tag show up as digest PRs.
- **`minimumGroupSize: 4`** counts Controller, Model, image, and targetAbi. Lower it to 3 if we decide not to track
  `targetAbi` (see §3). Without it, Renovate would open the PR with only the NuGet bumps because NuGet publishes first,
  CI would test against the old image, and the image bump would get added later.
- An alternative to the regex on a C# string: put the image in a `Dockerfile`/compose file under `tests/`. Renovate parses
  those natively, and Testcontainers could build or read from them. The regex approach keeps the test harness simpler.
- To be safe with the brand-new scheme, add `"matchUpdateTypes": ["major"], "dependencyDashboardApproval": true` for the
  Jellyfin group. A major means an ABI break and needs human work anyway.
- The pinned `.NET SDK` (`global.json`) and the `TargetFramework` (`net10.0`) are **not** in this group. TFM bumps only come
  with Jellyfin majors and are a manual change.

## 3. `targetAbi` semantics

From `jellyfin/jellyfin` source:
- `PluginManager.cs`: `return new LocalPlugin(dir, _appVersion >= targetAbi, manifest);`. An installed Plugin loads only if
  the server version is at least `targetAbi`.
- `InstallationManager.cs`: *"Only show plugins that are greater than or equal to targetAbi"*. Catalog versions with
  `targetAbi > ApplicationVersion` are removed. When installing, versions are filtered with `Version.Parse(x.TargetAbi) <= appVer`.
- There's **no upper bound**. 10.11 plugins fail on 12.0 because their types don't load (status `Malfunctioned`), not because
  of `targetAbi`.

So `targetAbi` is "minimum server version". The map's constraint is that we support only the latest stable release, and we
compile against `Jellyfin.Controller 12.N.0`. That means `targetAbi: "12.N.0.0"` is the honest value. It stops servers on
older 12.x releases from installing a build that may use APIs added later. Renovate's second custom manager keeps it in sync.
(Official plugins choose the looser `12.0.0.0`. That works for them because they also compile against floating `12.*`.)

## 4. Plugin repository `manifest.json` format

A JSON array of packages. Each package has a `versions` array. Field names come from `MediaBrowser.Model/Updates/VersionInfo.cs`
and the live repo.jellyfin.org manifest:

```json
[
  {
    "guid": "…",
    "name": "…",
    "description": "…",
    "overview": "…",
    "owner": "…",
    "category": "Authentication",
    "imageUrl": "… (optional)",
    "versions": [
      {
        "version": "1.0.0.0",
        "changelog": "…",
        "targetAbi": "12.4.0.0",
        "sourceUrl": "https://github.com/…/releases/download/v1.0.0.0/oidc_1.0.0.0.zip",
        "checksum": "<MD5 hex>",
        "timestamp": "2026-10-29T00:00:00Z"
      }
    ]
  }
]
```

- `checksum` is **MD5**. `InstallationManager` computes `MD5.HashDataAsync` over the downloaded zip and rejects the install
  if it doesn't match (case-insensitive hex).
- `repositoryName`/`repositoryUrl` are filled in by the server. Don't write them.
- The repository URL is added under Dashboard → Plugins → Repositories. It must point straight at the `manifest.json`
  ([Jellyfin plugin docs](https://jellyfin.org/docs/general/server/plugins/) list third-party repos this way).

## 5. jprm vs a custom GitHub Actions release

**jprm** ([oddstr13/jellyfin-plugin-repository-manager](https://github.com/oddstr13/jellyfin-plugin-repository-manager);
latest tag v1.1.1 2024-06, PyPI 1.1.0). It's still what Jellyfin's own pipeline runs.
- `jprm plugin build` reads `build.yaml` (name, guid, version, targetAbi, framework, artifacts, changelog), runs
  `dotnet publish`, and zips the listed artifacts plus a `meta.json`. The repo ships a composite **GitHub Action**
  (`action.yaml`, inputs `path`, `output`, `version`, `dotnet-target`, ...; output `artifact`).
- `jprm repo init <dir>` / `jprm repo add <manifest.json> <zip> [--url base | --plugin-url full-zip-url]` reads `meta.json`
  from the zip, computes the MD5, and **appends** a version entry. With `--plugin-url` it sets `sourceUrl` to that URL and
  doesn't copy the zip, so the binary can stay on GitHub Releases.

**Jellyfin's shared workflows** (`jellyfin/jellyfin-meta-plugins/.github/workflows/{build,publish}.yaml`, which the template
calls):
- `build`: setup-dotnet (default `10.0.x`), then the jprm action (`dotnet-target` default `net10.0`), then upload the artifact.
- `publish`: re-builds, writes `.md5`/`.sha256`, and attaches them to the GitHub Release. Then, **only for `jellyfin/*`
  repos**, it rsyncs over SSH to repo.jellyfin.org and runs `jprm repo add` there. This needs `DEPLOY_HOST/USER/KEY`
  secrets, so we can't use it for publishing. We could still call `build.yaml@master`, but pinning our own steps is more
  predictable.

**Community approaches:**
- `9p4/jellyfin-plugin-sso` (the reference Plugin): on `release: released` it builds (jprm), attaches the zip + md5/sha256
  with `softprops/action-gh-release`, then runs
  [`Kevinjil/jellyfin-plugin-repo-action`](https://github.com/Kevinjil/jellyfin-plugin-repo-action). That action rebuilds
  `manifest.json` statelessly from **all GitHub Releases** (zip asset → `sourceUrl`, `.md5` asset → checksum, release notes →
  changelog, `build.yaml` at tag) and commits it to a `manifest-release` branch served by GitHub Pages. The action is
  small (9 stars, last release v0.4.3 Feb 2025).
- `intro-skipper/intro-skipper`: fully custom (`dotnet build`, SignPath signing, SBOM, `gh release create`). The manifest is
  updated by their own `intro-skipper-action-ts`. That's too heavy for us.

### Recommended release pipeline

`release.yaml` on `release: { types: [published] }`. The tag is `vX.Y.Z.W`; prereleases are skipped or flagged.
1. checkout → `actions/setup-dotnet` (from `global.json`) → `oddstr13/jellyfin-plugin-repository-manager@<sha>` with
   `version: ${tag#v}`. The zip comes out with `meta.json` inside.
2. `md5sum` → upload the zip + `.md5` to the release (`gh release upload`).
3. checkout the `gh-pages` branch → `pip install jprm` (or use the vendored script) →
   `jprm repo add ./manifest.json <zip> --plugin-url https://github.com/nicoflink/jellyfin-oidc-plugin/releases/download/<tag>/<zip>`
   → commit + push.
4. GitHub Pages (branch source `gh-pages`) serves `https://nicoflink.github.io/jellyfin-oidc-plugin/manifest.json`.

This uses Jellyfin's own tooling and has no SSH or secrets beyond `GITHUB_TOKEN` (`contents: write`). The manifest is
git-versioned history. The trade-off against Kevinjil's action is that the manifest is **stateful** (append-only), where
Kevinjil's is regenerated from Releases. If we delete a release, we have to edit `manifest.json` by hand
(`jprm repo remove`). Kevinjil's action handles that automatically, but it's a small third-party dependency. Both work;
jprm is the higher-trust choice.

## Open questions surfaced

- **Is minor-to-minor 12.x truly ABI-stable?** Official plugins assume so (`targetAbi 12.0.0.0`, floating `12.*`). We compile
  against the exact minor and gate `targetAbi` on it, so we're safe either way. But it decides whether the `targetAbi`
  lockstep bump is worth it, compared with leaving it at `12.0.0.0`.
- **Should the Renovate Jellyfin group automerge** when the integration tests pass (minor only)? That ties into the
  test-harness ticket.

## Sources

- jellyfin/jellyfin releases & source: `Emby.Server.Implementations/Plugins/PluginManager.cs`, `Emby.Server.Implementations/Updates/InstallationManager.cs`, `MediaBrowser.Model/Updates/VersionInfo.cs`
- https://jellyfin.org/posts/jellyfin-release-12.0/
- https://jellyfin.org/docs/general/server/plugins/
- https://repo.jellyfin.org/files/plugin/manifest.json
- NuGet: https://api.nuget.org/v3-flatcontainer/jellyfin.controller/index.json
- Docker Hub: https://hub.docker.com/v2/repositories/jellyfin/jellyfin/tags
- https://github.com/jellyfin/jellyfin-plugin-template (`build.yaml`, csproj, `.github/renovate.json`, workflows)
- https://github.com/jellyfin/.github/tree/master/renovate-presets (`plugin.json`, `dotnet.json`, `default.json`)
- https://github.com/jellyfin/jellyfin-meta-plugins/tree/master/.github/workflows (`build.yaml`, `publish.yaml`)
- https://github.com/jellyfin/jellyfin-plugin-ldapauth (`build.yaml`, PR #234), https://github.com/jellyfin/jellyfin-plugin-opensubtitles (`build.yaml`)
- https://github.com/oddstr13/jellyfin-plugin-repository-manager (`README`, `action.yaml`, `jprm/__init__.py`)
- Renovate 44.196.0 docs: `docs/usage/configuration-options.md` (groupName, groupSlug, minimumGroupSize, managerFilePatterns, minimumReleaseAge), https://docs.renovatebot.com/modules/manager/regex/, https://docs.renovatebot.com/modules/manager/nuget/
- https://github.com/9p4/jellyfin-plugin-sso (`.github/workflows/publish.yml`), https://github.com/Kevinjil/jellyfin-plugin-repo-action
- https://github.com/intro-skipper/intro-skipper (`.github/workflows/release.yml`)
