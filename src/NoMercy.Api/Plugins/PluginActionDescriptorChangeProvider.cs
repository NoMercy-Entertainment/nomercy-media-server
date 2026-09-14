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

using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.Primitives;

namespace NoMercy.Api.Plugins;

/// <summary>
/// Tells MVC its route table is stale.
/// <para>
/// MVC builds the action-descriptor collection once and caches it. A plugin
/// installed or enabled after boot adds an <c>ApplicationPart</c> that the cache
/// has never seen, so without this its routes exist and are unreachable — which
/// reads as an installed plugin that silently does nothing.
/// </para>
/// </summary>
public class PluginActionDescriptorChangeProvider : IActionDescriptorChangeProvider
{
    private CancellationTokenSource _tokenSource = new();

    public IChangeToken GetChangeToken() => new CancellationChangeToken(_tokenSource.Token);

    public void TriggerChange()
    {
        CancellationTokenSource previous = Interlocked.Exchange(ref _tokenSource, new());
        previous.Cancel();
        previous.Dispose();
    }
}
