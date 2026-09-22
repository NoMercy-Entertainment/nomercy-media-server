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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// One open page.
/// <para>
/// Disposable because a page is a real browser tab holding real memory, and a
/// solver that opens one per attempt exhausts the host long before it gives up
/// on the challenge it was fighting.
/// </para>
/// </summary>
public interface IPluginBrowserPage : IAsyncDisposable
{
    Task GotoAsync(Uri url, CancellationToken ct = default);

    Task WaitForAsync(string selector, CancellationToken ct = default);

    Task<string> ContentAsync(CancellationToken ct = default);

    /// <summary>
    /// The cookies the page holds, so a plugin can carry a session on through
    /// its own HTTP calls rather than being handed a live browser to keep.
    /// </summary>
    Task<IReadOnlyList<PluginBrowserCookie>> CookiesAsync(CancellationToken ct = default);
}
