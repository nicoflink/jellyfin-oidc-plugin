using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Playwright;

namespace Jellyfin.Plugin.OidcLogin.E2ETests;

/// <summary>
/// A browser context with one page, whose Playwright trace is written to the artifacts directory on dispose.
/// </summary>
internal sealed class BrowserSession : IAsyncDisposable
{
    private readonly string _traceName;

    private BrowserSession(IBrowserContext context, IPage page, string traceName)
    {
        Context = context;
        Page = page;
        _traceName = traceName;
    }

    public IBrowserContext Context { get; }

    public IPage Page { get; }

    public static async Task<BrowserSession> StartAsync(IBrowserContext context, string traceName)
    {
        await context.Tracing.StartAsync(new() { Screenshots = true, Snapshots = true, Sources = true });
        return new BrowserSession(context, await context.NewPageAsync(), traceName);
    }

    public async ValueTask DisposeAsync()
    {
        var fileName = new string(_traceName.Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        await Context.Tracing.StopAsync(new() { Path = Path.Combine(E2EEnvironment.ArtifactsDirectory, "traces", $"{fileName}.zip") });
        await Context.DisposeAsync();
    }
}
