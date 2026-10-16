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
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Runtime;
using PuppeteerSharp;

namespace NoMercy.PluginSdk.Browser;

/// <summary>One lazy browser for one plugin. Pages release Chrome when the last tab closes.</summary>
internal sealed class PluginBrowser(
    Ulid pluginId,
    IPluginCapabilityBroker broker,
    PluginCapabilities? capabilities,
    Func<IReadOnlyList<string>> grantedHosts,
    IPluginResourceLedger ledger
) : IPluginBrowser, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<PluginBrowserPage> _pages = [];
    private HiddenChrome? _chrome;
    private bool _disposed;
    private string? _startFailure;

    internal bool IsDisposed => _disposed;

    public bool IsAvailable => UnavailableReason.Length == 0;

    public string UnavailableReason => _startFailure ?? HiddenChrome.Availability().Reason;

    public async Task<IPluginBrowserPage> OpenAsync(
        Uri url,
        PluginBrowserOptions options,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);
        CheckUrl(url);
        PluginRefusal? denied = broker.Check(pluginId, PluginCapabilityNames.BrowserHeadless);
        if (denied is not null)
            throw new PluginRefusedException(denied);

        await _gate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!IsAvailable)
                throw new InvalidOperationException(UnavailableReason);

            if (_chrome is null)
            {
                try
                {
                    _chrome = await HiddenChrome.StartAsync(ct);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    _startFailure = error.Message;
                    throw;
                }
                ledger.Track(pluginId, this);
            }

            IPage? page = null;
            try
            {
                page = await _chrome.Browser.NewPageAsync().WaitAsync(ct);
                PluginBrowserPage wrapper = new(this, page, options);
                await wrapper.ConfigureAsync(ct);
                _pages.Add(wrapper);
                await wrapper.GotoAsync(url, ct);
                return wrapper;
            }
            catch
            {
                try
                {
                    if (page is not null)
                    {
                        _pages.RemoveWhere(wrapper => ReferenceEquals(wrapper.Page, page));
                        await page.CloseAsync();
                    }
                }
                finally
                {
                    if (_pages.Count == 0)
                        await StopAsync();
                }
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal bool Allows(Uri url) =>
        url.IsAbsoluteUri
        && url.Scheme is "http" or "https"
        && (
            PluginScopeGlob.AnyMatches(capabilities?.Network?.Hosts ?? [], url.Host)
            || PluginScopeGlob.AnyMatches(grantedHosts(), url.Host)
        )
        && broker.Check(pluginId, PluginCapabilityNames.NetworkFetch, url.Host) is null;

    internal void CheckUrl(Uri url)
    {
        if (!Allows(url))
            throw new PluginRefusedException(
                PluginRefusalMessages.BrowserNavigationBlocked(pluginId.ToString(), url.ToString())
            );
    }

    internal async Task ReleaseAsync(PluginBrowserPage page)
    {
        await _gate.WaitAsync();
        try
        {
            if (!_pages.Remove(page))
                return;

            try
            {
                await page.CloseAsync();
            }
            finally
            {
                if (_pages.Count == 0)
                    await StopAsync();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAsync()
    {
        if (_chrome is null)
            return;

        HiddenChrome chrome = _chrome;
        _chrome = null;
        ledger.Forget(pluginId, this);
        await chrome.DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync();
        try
        {
            _disposed = true;
            foreach (PluginBrowserPage page in _pages)
            {
                try
                {
                    await page.CloseAsync();
                }
                catch (Exception)
                {
                    // The browser may have exited; continue releasing its other tabs.
                }
            }
            _pages.Clear();
            await StopAsync();
        }
        finally
        {
            _gate.Release();
        }
    }
}
