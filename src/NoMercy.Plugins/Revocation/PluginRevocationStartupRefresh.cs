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

using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Verification;

namespace NoMercy.PluginSdk.Revocation;

/// <summary>
/// Asks for the revocation list once before the start-up plugin load, for a
/// bounded time. A server that gets no answer loads with the stored list (AUD-1134).
/// </summary>
public class PluginRevocationStartupRefresh(
    PluginMarketplaceKeyClient keys,
    PluginRevocationClient revocations,
    PluginTrustAddresses addresses,
    ILogger<PluginRevocationStartupRefresh> logger,
    TimeSpan? bound = null
)
{
    public static TimeSpan Bound { get; } = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _bound = bound ?? Bound;

    public async Task RefreshBeforeLoadAsync(CancellationToken ct = default)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_bound);

        try
        {
            // Keys first: the list is checked against them.
            await keys.RefreshAsync(addresses.Keys, limit.Token);
            await revocations.RefreshAsync(addresses.Revocations, limit.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning(
                "Plugin revocations: no answer within {Seconds} seconds at start-up, so plugins load with the list already on this server.",
                _bound.TotalSeconds.ToString(CultureInfo.InvariantCulture)
            );
        }
    }
}
