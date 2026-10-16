// -----------------------------------------------------------------------------
//  Copyright (c) 2024-present NoMercy Entertainment. All rights reserved.
//
//  This file is part of NoMercy MediaServer, source-available software (NOT open
//  source). Personal use and contributions are welcome; distribution, resale,
//  relicensing, and commercial exploitation are prohibited without explicit
//  written consent. See LICENSE for full terms. Distributed WITHOUT ANY WARRANTY.
//
//  SPDX-License-Identifier: LicenseRef-NoMercy-Proprietary
// -----------------------------------------------------------------------------

using NoMercy.PluginSdk.Abstractions;
using PuppeteerSharp;

namespace NoMercy.PluginSdk.Browser;

internal sealed class PluginBrowserPage(
    PluginBrowser owner,
    IPage page,
    PluginBrowserOptions options
) : IPluginBrowserPage
{
    private int _disposed;

    internal IPage Page => page;

    internal async Task ConfigureAsync(CancellationToken ct)
    {
        if (options.UserAgent is not null)
            await page.SetUserAgentAsync(options.UserAgent).WaitAsync(ct);
        await page.SetExtraHttpHeadersAsync(
                new Dictionary<string, string> { ["Accept-Language"] = options.Locale }
            )
            .WaitAsync(ct);
        await page.SetRequestInterceptionAsync(true).WaitAsync(ct);
        page.AddRequestInterceptor(async request =>
        {
            if (Uri.TryCreate(request.Url, UriKind.Absolute, out Uri? uri) && owner.Allows(uri))
                await request.ContinueAsync();
            else
                await request.AbortAsync();
        });
    }

    public async Task GotoAsync(Uri url, CancellationToken ct = default)
    {
        owner.CheckUrl(url);
        await page.GoToAsync(
                url.ToString(),
                new NavigationOptions
                {
                    Timeout = (int)options.Timeout.TotalMilliseconds,
                    WaitUntil = [WaitUntilNavigation.DOMContentLoaded],
                    Referer = options.Referer,
                }
            )
            .WaitAsync(ct);
    }

    public async Task ReloadAsync(CancellationToken ct = default) =>
        await page.ReloadAsync(
                (int)options.Timeout.TotalMilliseconds,
                [WaitUntilNavigation.DOMContentLoaded]
            )
            .WaitAsync(ct);

    public async Task WaitForAsync(string selector, CancellationToken ct = default) =>
        await DuringNavigationAsync(
            () =>
                page.WaitForSelectorAsync(
                    selector,
                    new WaitForSelectorOptions { Timeout = (int)options.Timeout.TotalMilliseconds }
                ),
            null,
            ct
        );

    public Task<string> ContentAsync(CancellationToken ct = default) =>
        DuringNavigationAsync(() => page.GetContentAsync(), string.Empty, ct);

    public Task<string> ContentTypeAsync(CancellationToken ct = default) =>
        DuringNavigationAsync(
            () => page.EvaluateExpressionAsync<string>("document.contentType"),
            string.Empty,
            ct
        );

    public Task<bool> IsReadableAsync(CancellationToken ct = default) =>
        DuringNavigationAsync(
            () =>
                page.EvaluateExpressionAsync<bool>(
                    "document.readyState !== 'loading' && !!document.body && document.body.children.length > 0"
                ),
            false,
            ct
        );

    public Task<string> UserAgentAsync(CancellationToken ct = default) =>
        DuringNavigationAsync(
            () => page.EvaluateExpressionAsync<string>("navigator.userAgent"),
            string.Empty,
            ct
        );

    public Task<string> FetchTextAsync(Uri url, CancellationToken ct = default)
    {
        owner.CheckUrl(url);
        return DuringNavigationAsync(
            () =>
                page.EvaluateFunctionAsync<string>(
                    "async url => { const response = await fetch(url, { credentials: 'include' }); return await response.text(); }",
                    url.ToString()
                ),
            string.Empty,
            ct
        );
    }

    public async Task<IReadOnlyList<PluginBrowserCookie>> CookiesAsync(
        CancellationToken ct = default
    )
    {
        CookieParam[] cookies = await DuringNavigationAsync(() => page.GetCookiesAsync(), [], ct);
        return cookies
            .Select(cookie => new PluginBrowserCookie(
                cookie.Name,
                cookie.Value,
                cookie.Domain,
                cookie.Path,
                cookie.Expires is null or <= 0
                    ? null
                    : DateTimeOffset.FromUnixTimeSeconds((long)cookie.Expires),
                cookie.Secure ?? false
            ))
            .ToArray();
    }

    internal static async Task<T> DuringNavigationAsync<T>(
        Func<Task<T>> read,
        T notReady,
        CancellationToken ct
    )
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                return await read().WaitAsync(ct);
            }
            catch (PuppeteerException error)
                when (error.Message.Contains(
                        "Execution context was destroyed",
                        StringComparison.OrdinalIgnoreCase
                    )
                    || error.Message.Contains(
                        "Cannot find context",
                        StringComparison.OrdinalIgnoreCase
                    )
                )
            {
                if (attempt == 0)
                    await Task.Delay(50, ct);
            }
        }

        return notReady;
    }

    internal Task CloseAsync() => page.CloseAsync();

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _disposed, 1) == 0
            ? new ValueTask(owner.ReleaseAsync(this))
            : ValueTask.CompletedTask;
}
