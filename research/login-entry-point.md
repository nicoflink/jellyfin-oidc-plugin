# Research: Login Entry Point options in the Jellyfin web UI

Ticket: [#3](https://github.com/nicoflink/jellyfin-oidc-plugin/issues/3) · Researched 2026-10-29

**Question:** Besides an HTML link in Branding → Login disclaimer, what ways exist on current Jellyfin to put an OIDC Login button on the login page? For each: robustness across Jellyfin upgrades, admin setup effort, and whether it survives the Renovate-driven upgrade cadence.

## Baseline facts

- **Current stable is Jellyfin 12.4** (server and web, released 2026-10-26). Jellyfin went from 10.11.x to **12.0** on 2026-09-08 (.NET 10). Releases since then: 12.1 (09-15), 12.2 (10-05), 12.3 (10-23), 12.4 (10-26). That is five releases in seven weeks, so Renovate will bump often. ([server releases](https://github.com/jellyfin/jellyfin/releases), [12.0 notes](https://github.com/jellyfin/jellyfin/releases/tag/v12.0))
- Sources pinned for this research: jellyfin-web `v12.4` @ [`0a580ce`](https://github.com/jellyfin/jellyfin-web/tree/0a580ce8df89282dfd120b6d5efb8e106da23bd5), jellyfin `v12.4` @ [`83d0d0e`](https://github.com/jellyfin/jellyfin/tree/83d0d0e7f76e23e2e085c8ff67d28b772d35967d), File Transformation `main` @ [`2dd4279`](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/tree/2dd4279bc7aedd082de0639278f9142ead229a52), 9p4/jellyfin-plugin-sso `main` @ [`f1e157b`](https://github.com/9p4/jellyfin-plugin-sso/tree/f1e157bac29e6d27a0844378f3e1d4a4fdaa577a).
- The login page is still the **legacy (non-React) controller** in both the Modern and the Legacy layouts. Both route tables point at `session/login/index`. ([modern public routes](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/apps/modern/routes/legacyRoutes/public.ts#L21-L22))
- 12.0 shipped **no new web-UI or login extension points for plugins**. The "New plugin APIs" section lists only search, similarity, comic, chapter, password-reset, segment, Live TV, linked-children, batch and localization APIs. ([12.0 notes, "New plugin APIs"](https://github.com/jellyfin/jellyfin/releases/tag/v12.0))

## How the login disclaimer is rendered (12.4)

From [`session/login/index.js`](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/apps/legacy/controllers/session/login/index.js#L25-L29) and [L302-L319](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/apps/legacy/controllers/session/login/index.js#L302-L319):

1. The page fetches the anonymous endpoint `GET /Branding/Configuration` and renders `LoginDisclaimer` as Markdown with `html: true`.
2. The output goes through **DOMPurify 2.5.9** with a custom `ALLOWED_URI_REGEXP`. 12.0 added this regex ("The login disclaimer only allows common link protocol schemes"). It allows `https?`, `mailto`, `tel` and a few messengers, plus **relative URLs** (`[^a-z]` prefix). `javascript:` and other schemes are dropped.
3. DOMPurify's default allowlist strips `<script>` and `on*` handlers. It **keeps `<form>`, `<button>`, and the `action`/`method` attributes** ([tags.js@2.5.9](https://github.com/cure53/DOMPurify/blob/2.5.9/src/tags.js), [attrs.js@2.5.9](https://github.com/cure53/DOMPurify/blob/2.5.9/src/attrs.js)).
4. **Every `<a>` inside the disclaimer is rewritten** to `target="_blank" rel="noopener noreferrer"`. A plain link therefore starts the OIDC Login in a **new tab**. The user finishes the flow there and the original tab stays on the login page.
   - Upstream issue [jellyfin-web#8357](https://github.com/jellyfin/jellyfin-web/issues/8357) reports exactly this for SSO links. Open PR [jellyfin-web#8360](https://github.com/jellyfin/jellyfin-web/pull/8360) would keep same-origin links in the same tab. It is unreviewed and unmerged as of 2026-10-29.
   - **Workaround that works today:** a `<form action="…"><button …>` is not touched by the `<a>` loop, so it navigates in the **same tab**. This is why 9p4's README uses a form ([README L98-L121](https://github.com/9p4/jellyfin-plugin-sso/blob/f1e157bac29e6d27a0844378f3e1d4a4fdaa577a/README.md#L98-L121)).
5. The container markup is `<div class="loginDisclaimerContainer"><div class="loginDisclaimer">` ([index.html L52-L54](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/apps/legacy/controllers/session/login/index.html#L52-L54)). The class was renamed from `disclaimerContainer` in 2023 because ad blockers hid it. 9p4's README still uses the old class name ([9p4 issue #298](https://github.com/9p4/jellyfin-plugin-sso/issues/298)), which shows how CSS-selector-based setups drift without anyone noticing.

## Options

### A. Login disclaimer HTML: link or form (the baseline)

- **Mechanism:** the admin pastes a snippet into Dashboard → Branding → Login disclaimer. Prefer `<form action="<base>/<plugin-route>/start" method="get"><button class="raised block emby-button button-submit">Sign in with PocketID</button></form>` over `<a>`, because it keeps the flow in the same tab.
- **Robustness:** high. `LoginDisclaimer` is a documented server setting ([docs](https://jellyfin.org/docs/general/server/settings/#login-disclaimer)) and part of the public `BrandingOptionsDto`. Markdown support dates from 10.8 (2022). The only regressions so far have been tightening, never removal:
  - the 12.0 URI scheme allowlist (does not affect `https` or relative URLs);
  - the forced `_blank` on `<a>`.
- **Risk:** a future sanitizer change could drop `<form>`. The `<a>` fallback keeps working, but in a new tab.
- **Admin effort:** one paste, plus the correct absolute or base-URL-aware path.
- **Renovate cadence:** unaffected. There is no version coupling.

### B. Branding Custom CSS (cosmetic only)

- **Mechanism:** Branding → Custom CSS is injected as a `<style>` element on every page, including login ([`CustomCss.tsx`](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/components/CustomCss.tsx)). Each user can turn it off with "Disable server-provided custom CSS".
- **Limits:** CSS cannot create a navigation target. It can only style or reorder option A's link or button, or hide the Local Login form. Hiding Local Login conflicts with the settled "Local Login always stays available" constraint, so this plugin should not do that.
- **Robustness:** medium-low. It depends on undocumented class names (`.loginDisclaimerContainer`, `.readOnlyContent`, `.manualLoginForm`), and those have been renamed before. 12.0 also rebased all themes on CSS variables ("Custom themes may need to be adjusted").
- **Admin effort:** one paste. **Renovate cadence:** silent cosmetic breakage is possible, but the button never stops working functionally.

### C. Plugin edits the Branding config itself (option A, automated)

- **Mechanism:** the server exposes branding through `IServerConfigurationManager.GetConfiguration<BrandingOptions>("branding")` / `SaveConfiguration("branding", …)`. Core code already uses these ([BrandingController](https://github.com/jellyfin/jellyfin/blob/83d0d0e7f76e23e2e085c8ff67d28b772d35967d/Jellyfin.Api/Controllers/BrandingController.cs#L30-L34), [ImageController L1738-L1740](https://github.com/jellyfin/jellyfin/blob/83d0d0e7f76e23e2e085c8ff67d28b772d35967d/Jellyfin.Api/Controllers/ImageController.cs#L1738-L1740)). The Plugin could insert or update a marked snippet, such as `<!-- oidc-login:start -->…<!-- oidc-login:end -->`, when the admin ticks "Show OIDC Login button on login page" on the plugin config page. It would generate the correct base-URL-aware `action` and remove the snippet when unticked or uninstalled.
- **Robustness:** same as A, because it relies only on public server-side config APIs that plugins can reference.
- **Admin effort:** one checkbox.
- **Risk:** the Plugin writes into an admin-owned field, so it must preserve the admin's other disclaimer text. The marker approach handles that.
- **Renovate cadence:** unaffected.

### D. File Transformation plugin + injected JS

- **Mechanism:** [File Transformation](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation) (FT) registers an ASP.NET Core `IStartupFilter` from its `IPluginServiceRegistrator` ([PluginServiceRegistrator.cs](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2dd4279bc7aedd082de0639278f9142ead229a52/src/Jellyfin.Plugin.FileTransformation/PluginServiceRegistrator.cs), [StartupFilter](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/blob/2dd4279bc7aedd082de0639278f9142ead229a52/src/Jellyfin.Plugin.FileTransformation/Infrastructure/FileTransformationStartupFilter.cs)). The filter buffers and rewrites any `/web/*` response for which another plugin has registered a regex and callback. Other plugins register by **reflection** across load contexts, not by a compile-time reference (README "Referencing this as a library"). Our Plugin would rewrite `index.html` to add a `<script>`. That script would watch for `#loginPage` and insert a native-looking button into `.readOnlyContent`.
- **Robustness:**
  - **Low across web upgrades:** the script is coupled to undocumented DOM and ids. The login controller moved paths in 2026-06, a TypeScript migration of it is pending ([jellyfin-web#8228](https://github.com/jellyfin/jellyfin-web/pull/8228)), and an eventual React rewrite would change the DOM.
  - **Medium across server upgrades:** `IStartupFilter` is standard ASP.NET Core, but Jellyfin does not document it as a plugin extension point. An upstream attempt to make file transformation official was closed unmerged ([jellyfin#9095](https://github.com/jellyfin/jellyfin/pull/9095)).
- **Admin effort:** install a second plugin from a third-party repository (`iamparadox.dev`).
- **Renovate cadence:** **poor.** FT ships one build per Jellyfin minor version and says it is "strictly 1 version compatible". Its 12.x build lag was 0 days (12.0, 12.1), 7 days (12.2), 5 days (12.3) and 2 days (12.4) ([FT releases](https://github.com/IAmParadox27/jellyfin-plugin-file-transformation/releases)). Each Renovate PR would block on a third party, and the integration tests would also need to pull FT.
  - The *server* only enforces `targetAbi` as a lower bound (`_appVersion >= targetAbi`, [PluginManager.cs L745-L755](https://github.com/jellyfin/jellyfin/blob/83d0d0e7f76e23e2e085c8ff67d28b772d35967d/Emby.Server.Implementations/Plugins/PluginManager.cs#L745-L755)). So an older FT build may still load after a patch bump, but nobody supports that.
  - The single maintainer also announces irregular availability (FT README).

### E. Our own Plugin injects JS via its own `IStartupFilter` (FT technique, no FT dependency)

- **Mechanism:** this is the technique FT uses, about 100 lines, built into our Plugin. One variant is less fragile than DOM scripting: rewrite only the JSON of the anonymous `GET /Branding/Configuration` response to append the button snippet to `LoginDisclaimer` on the fly. This gives option C's result without persisting anything, and it relies on the public DTO shape instead of web DOM.
- **Robustness:** the JS-in-`index.html` variant is as fragile as D on the web side. The Branding-JSON variant is medium: the DTO is stable, but it still depends on the undocumented `IStartupFilter` hook and on middleware ordering. Jellyfin's pipeline is still `Startup.Configure` with `UseBaseUrlRedirection` followed by `app.Map(BaseUrl, …)` ([Startup.cs L168-L231](https://github.com/jellyfin/jellyfin/blob/83d0d0e7f76e23e2e085c8ff67d28b772d35967d/Jellyfin.Server/Startup.cs#L168-L231)). A move to minimal hosting could change that.
- **Admin effort:** zero.
- **Renovate cadence:** no third-party lag. Breakage would show up in our own integration tests.

### F. jellyfin-web client plugins (`config.json` `plugins`)

- **Mechanism:** the web client loads plugins listed in the static `web/config.json`, either through `window[...]` definitions or bundled dynamic imports ([`index.jsx` loadPlugins](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/index.jsx#L151-L177), [`pluginManager.js`](https://github.com/jellyfin/jellyfin-web/blob/0a580ce8df89282dfd120b6d5efb8e106da23bd5/src/components/pluginManager.js#L63-L106)). These are players, screensavers and similar types. A server plugin cannot add to the list without file rewriting (D or E), and the admin would have to edit files inside the container. **Not viable.**

### G. Plugin config pages (`IHasWebPages` / `PluginPageInfo`)

- These pages appear only in the authenticated dashboard or main menu (`EnableInMainMenu`, `MenuSection`) ([PluginPageInfo.cs](https://github.com/jellyfin/jellyfin/blob/83d0d0e7f76e23e2e085c8ff67d28b772d35967d/MediaBrowser.Model/Plugins/PluginPageInfo.cs)), so they never show on the login page. **Not viable** as a Login Entry Point, but this is where the setting for option C goes.

### H. Direct URL / bookmark (complement, not a button)

- The Plugin's start route, for example `https://jellyfin.example.com/<plugin>/start`, can be bookmarked or linked from a reverse proxy or homepage dashboard. It has zero coupling and survives every upgrade. It is worth documenting as a second Login Entry Point next to the button.

### Upstream changes to watch

- [jellyfin-web#8360](https://github.com/jellyfin/jellyfin-web/pull/8360) (same-origin disclaimer links in the same tab): if merged, a plain `<a>` becomes as good as the `<form>`.
- [jellyfin-web#8228](https://github.com/jellyfin/jellyfin-web/pull/8228) (login controller to TypeScript): could change the sanitizer or link handling. The integration test should assert that the button is present and clickable.
- No upstream SSO/OIDC or login-hook work was found in jellyfin/jellyfin open PRs.

## Comparison

| Option | Shows a button | Robust across upgrades | Admin effort | Renovate cadence |
|---|---|---|---|---|
| A: Disclaimer `<form>` / `<a>` | yes (`<a>` opens a new tab) | high (documented setting) | paste snippet | unaffected |
| B: Custom CSS | styling only | med-low (class names) | paste snippet | silent cosmetic drift |
| C: Plugin writes Branding config | yes (same as A) | high (public config API) | tick a checkbox | unaffected |
| D: File Transformation + JS | yes, native-looking | low (DOM + 3rd-party hook) | install 2nd plugin + 3rd-party repo | blocks 0–7 days per release |
| E: Own `IStartupFilter` | yes | low (DOM) / medium (Branding JSON) | none | own tests catch breakage |
| F: Web client plugins | — | — | edit container files | not viable |
| G: Plugin config pages | no (dashboard only) | — | — | not viable |
| H: Direct URL | no button | highest | none | unaffected |

## Recommendation

1. **Login Entry Point = option C on top of A:** a plugin-managed, marker-delimited `<form>` button in the Login disclaimer, toggled from the plugin config page, with the `action` generated from the server's base URL.
   - The snippet is the same thing an admin could paste by hand, so the README documents the manual form as the fallback.
   - Use `<form>`, not `<a>`, so the OIDC Login stays in the same tab until jellyfin-web#8360 lands.
2. Ship an optional small **Custom CSS snippet** in the README for styling only. Never hide Local Login.
3. Document the **direct start URL** (H) as the zero-coupling Login Entry Point.
4. **Reject D:** the File Transformation dependency is incompatible with "latest stable only + Renovate group bumps", because of third-party lag and a single maintainer. Defer E: it is not needed while the disclaimer route works.
5. The integration test should load the login page as an anonymous user, find the button by its form `action`, and click it. That turns any upstream sanitizer or DOM change into a red Renovate PR instead of a silent regression.
