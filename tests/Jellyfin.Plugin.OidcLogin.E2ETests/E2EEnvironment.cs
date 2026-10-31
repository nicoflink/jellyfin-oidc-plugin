using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Jellyfin.Plugin.OidcLogin.E2ETests;
using Microsoft.Playwright;
using Xunit;

[assembly: AssemblyFixture(typeof(E2EEnvironment))]

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// One real PocketID and one real Jellyfin (with the Plugin installed) shared by every test in the run.
/// </summary>
/// <remarks>
/// Jellyfin joins PocketID's network namespace, so both reach the issuer at the same
/// <c>http://localhost:P</c> the browser uses (WebAuthn needs that secure origin). PocketID owns the
/// namespace and publishes both ports. The browser reaches Jellyfin as <c>127.0.0.1</c> so the two
/// apps' cookies stay apart.
/// </remarks>
public sealed class E2EEnvironment : IAsyncLifetime
{
    public const string JellyfinAdminName = "admin";
    public const string JellyfinAdminPassword = "e2e-admin-password";
    public const string OidcClientId = "jellyfin";
    public const string OidcClientSecret = "e2e-client-secret-0123456789";
    public const string JellyfinUsersGroup = "jellyfin-users";
    public const string JellyfinAdminsGroup = "jellyfin-admins";

    private const string StaticApiKey = "e2e-static-api-key-0123456789";
    private const int JellyfinPort = 8096;

    private IContainer? _pocketId;
    private IContainer? _jellyfin;
    private IPlaywright? _playwright;
    private string _usersGroupId = null!;
    private string _adminsGroupId = null!;

    internal static PocketIdUser Alice { get; } = new("6a3f1c2e-8b4d-4e5f-9a1b-2c3d4e5f6a7b", "alice");

    internal Uri PocketIdUrl { get; private set; } = null!;

    internal Uri JellyfinUrl { get; private set; } = null!;

    internal IBrowser Browser { get; private set; } = null!;

    /// <summary>
    /// Gets Alice's passkey, enrolled at PocketID through its UI and captured from the virtual authenticator.
    /// </summary>
    internal VirtualCredential AlicePasskey { get; private set; } = null!;

    internal static string ArtifactsDirectory { get; } =
        Environment.GetEnvironmentVariable("E2E_ARTIFACTS_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(AppContext.BaseDirectory, "e2e-artifacts");

    public async ValueTask InitializeAsync()
    {
        try
        {
            await StartAndSeedAsync();
        }
        catch
        {
            // xUnit doesn't dispose a fixture whose initialization threw; keep the logs and don't leak containers.
            await DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Fetches <paramref name="url"/> from inside the Jellyfin container, as the Plugin would.
    /// </summary>
    internal async Task<string> FetchFromJellyfinAsync(Uri url)
    {
        var result = await _jellyfin!.ExecAsync(["curl", "-sSf", url.ToString()]);
        Assert.True(result.ExitCode == 0, $"curl {url} from Jellyfin failed: {result.Stderr}");
        return result.Stdout;
    }

    /// <summary>
    /// Reads the Plugin's Linked Identity file from inside the Jellyfin container.
    /// </summary>
    internal async Task<string> ReadLinkedIdentitiesAsync()
    {
        var result = await _jellyfin!.ExecAsync(["sh", "-c", "cat \"$(find /config -name linked-identities.json | head -n 1)\""]);
        Assert.True(result.ExitCode == 0, $"Reading linked-identities.json from Jellyfin failed: {result.Stderr}");
        return result.Stdout;
    }

    /// <summary>
    /// Sets Alice's PocketID groups: always <see cref="JellyfinUsersGroup"/>, plus <see cref="JellyfinAdminsGroup"/>
    /// when <paramref name="admin"/> is set.
    /// </summary>
    internal async Task SetAliceGroupsAsync(bool admin)
    {
        using var pocketId = new PocketIdAdmin(PocketIdUrl, StaticApiKey);
        await pocketId.SetUserGroupsAsync(Alice.Id, admin ? [_usersGroupId, _adminsGroupId] : [_usersGroupId], TestContext.Current.CancellationToken);
    }

    private async Task StartAndSeedAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        var cancellationToken = timeout.Token;
        Directory.CreateDirectory(ArtifactsDirectory);

        var pocketIdPort = GetFreeTcpPort();
        PocketIdUrl = new Uri($"http://localhost:{pocketIdPort}/");
        var pocketIdContainer = new ContainerBuilder(ContainerImages.PocketId)
            .WithEnvironment("PORT", pocketIdPort.ToString(CultureInfo.InvariantCulture))
            .WithEnvironment("APP_URL", PocketIdUrl.ToString().TrimEnd('/'))
            .WithEnvironment("STATIC_API_KEY", StaticApiKey)
            .WithEnvironment("ENCRYPTION_KEY", "e2e-encryption-key-0123456789")
            .WithEnvironment("APP_ENV", "test")
            .WithPortBinding(pocketIdPort, pocketIdPort)
            .WithPortBinding(JellyfinPort, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(
                r => r.ForPort((ushort)pocketIdPort).ForPath("/healthz").ForStatusCode(HttpStatusCode.NoContent),
                w => w.WithTimeout(TimeSpan.FromMinutes(1))))
            .Build();
        _pocketId = pocketIdContainer;
        await pocketIdContainer.StartAsync(cancellationToken);

        JellyfinUrl = new Uri($"http://127.0.0.1:{pocketIdContainer.GetMappedPublicPort(JellyfinPort)}/");
        var jellyfinBuilder = new ContainerBuilder(ContainerImages.Jellyfin)
            .WithCreateParameterModifier(p => (p.HostConfig ??= new()).NetworkMode = $"container:{pocketIdContainer.Id}");
        foreach (var artifact in BuildManifest.Artifacts)
        {
            jellyfinBuilder = jellyfinBuilder.WithResourceMapping(new FileInfo(Path.Combine(AppContext.BaseDirectory, artifact)), "/config/plugins/OidcLogin/");
        }

        _jellyfin = jellyfinBuilder.Build();
        await _jellyfin.StartAsync(cancellationToken);

        using (var jellyfin = new JellyfinServer(JellyfinUrl))
        {
            await jellyfin.WaitUntilStartupWizardIsReadyAsync(TimeSpan.FromMinutes(3), cancellationToken);
            await jellyfin.CompleteStartupWizardAsync(JellyfinAdminName, JellyfinAdminPassword, cancellationToken);
            var adminToken = await jellyfin.AuthenticateAsync(JellyfinAdminName, JellyfinAdminPassword, cancellationToken);
            await jellyfin.PostAsAdminAsync(
                $"Plugins/{BuildManifest.PluginId}/Configuration",
                new
                {
                    PublicJellyfinUrl = JellyfinUrl.ToString(),
                    IssuerUrl = PocketIdUrl.ToString().TrimEnd('/'),
                    ClientId = OidcClientId,
                    ClientSecret = OidcClientSecret,
                    AdminGroup = JellyfinAdminsGroup,
                    ShowSignInButton = true,
                },
                adminToken,
                cancellationToken);
        }

        using (var pocketId = new PocketIdAdmin(PocketIdUrl, StaticApiKey))
        {
            _usersGroupId = await pocketId.CreateGroupAsync(JellyfinUsersGroup, cancellationToken);
            _adminsGroupId = await pocketId.CreateGroupAsync(JellyfinAdminsGroup, cancellationToken);
            // The callback is registered without a port: PocketID matches loopback callbacks on any port (RFC 8252).
            await pocketId.CreateOidcClientAsync(OidcClientId, OidcClientSecret, new Uri("http://127.0.0.1/OidcLogin/Callback"), [_usersGroupId, _adminsGroupId], cancellationToken);
            await pocketId.CreateUserAsync(Alice, cancellationToken);
            await pocketId.SetUserGroupsAsync(Alice.Id, [_usersGroupId], cancellationToken);

            InstallPlaywrightChromium();
            _playwright = await Playwright.CreateAsync();
            Browser = await _playwright.Chromium.LaunchAsync();
            AlicePasskey = await EnrolPasskeyAsync(pocketId, Alice, cancellationToken);
        }
    }

    /// <summary>
    /// Opens a fresh browser context, traced to the artifacts directory, optionally holding Alice's passkey.
    /// </summary>
    internal async Task<BrowserSession> NewSessionAsync(bool withAlicePasskey = false)
    {
        var context = await NewContextAsync();
        if (withAlicePasskey)
        {
            await context.Credentials.InstallAsync();
            await context.Credentials.CreateAsync(AlicePasskey.RpId, new()
            {
                Id = AlicePasskey.Id,
                UserHandle = AlicePasskey.UserHandle,
                PrivateKey = AlicePasskey.PrivateKey,
                PublicKey = AlicePasskey.PublicKey,
                SignCount = AlicePasskey.SignCount,
            });
        }

        return await BrowserSession.StartAsync(context, TestContext.Current.Test?.TestDisplayName ?? "session");
    }

    public async ValueTask DisposeAsync()
    {
        // Safe to call twice: InitializeAsync disposes on failure, and xUnit may dispose again.
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
            Browser = null!;
        }

        _playwright?.Dispose();
        _playwright = null;
        await SaveLogsAndDisposeAsync(_jellyfin, "jellyfin");
        _jellyfin = null;
        await SaveLogsAndDisposeAsync(_pocketId, "pocketid");
        _pocketId = null;
    }

    /// <summary>
    /// Opens a browser context in a fixed locale, so the UIs under test don't follow the host's language.
    /// </summary>
    private Task<IBrowserContext> NewContextAsync() => Browser.NewContextAsync(new() { Locale = "en-US" });

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static void InstallPlaywrightChromium()
    {
        // Idempotent and fast once installed; CI installs it with system dependencies up front.
        var exitCode = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"Installing Playwright Chromium failed with exit code {exitCode}.");
        }
    }

    private static async Task SaveLogsAndDisposeAsync(IContainer? container, string name)
    {
        if (container is null)
        {
            return;
        }

        try
        {
            var (stdout, stderr) = await container.GetLogsAsync();
            await File.WriteAllTextAsync(Path.Combine(ArtifactsDirectory, $"{name}.log"), stdout + stderr);
        }
        finally
        {
            await container.DisposeAsync();
        }
    }

    /// <summary>
    /// Signs <paramref name="user"/> in to PocketID with a one-time access token, adds a passkey through
    /// PocketID's UI, and returns it as captured by the virtual authenticator.
    /// </summary>
    private async Task<VirtualCredential> EnrolPasskeyAsync(PocketIdAdmin pocketId, PocketIdUser user, CancellationToken cancellationToken)
    {
        var token = await pocketId.CreateOneTimeAccessTokenAsync(user.Id, cancellationToken);
        var context = await NewContextAsync();
        await context.Credentials.InstallAsync();
        await using var session = await BrowserSession.StartAsync(context, "fixture-passkey-enrolment");
        var page = session.Page;

        // A user without a passkey lands on PocketID's "set up your passkey" step after signing in.
        await page.GotoAsync(new Uri(PocketIdUrl, $"lc/{token}").ToString());
        await page.WaitForURLAsync(url => url.Contains("/signup/add-passkey", StringComparison.Ordinal));
        await page.GetByRole(AriaRole.Button, new() { Name = "Add Passkey" }).ClickAsync();
        await page.WaitForURLAsync(url => url.Contains("/settings/account", StringComparison.Ordinal));

        var host = PocketIdUrl.Host;
        for (var attempt = 0; attempt < 50; attempt++)
        {
            var credentials = await context.Credentials.GetAsync(new() { RpId = host });
            if (credentials.Count > 0)
            {
                return credentials.Single();
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken);
        }

        throw new InvalidOperationException("PocketID never registered a passkey with the virtual authenticator.");
    }
}
