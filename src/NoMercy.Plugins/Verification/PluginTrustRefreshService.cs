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

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Auth;
using NoMercy.PluginSdk.Entitlements;
using NoMercy.PluginSdk.Revocation;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// Refreshes the key set, then the revocation list, then the entitlement
/// bundle: on start and every six hours.
/// <para>
/// Keys first, so a list signed by a key published since the last run
/// verifies on this run rather than the next. Every step keeps what it had
/// when the answer is wrong or missing; none of them can stop the server.
/// Entitlements need the server's token and are skipped until it has one.
/// </para>
/// </summary>
public class PluginTrustRefreshService(
    PluginMarketplaceKeyClient keys,
    PluginRevocationClient revocations,
    PluginEntitlementClient entitlements,
    IAuthTokenStore tokens,
    PluginTrustAddresses addresses,
    ILogger<PluginTrustRefreshService> logger
) : BackgroundService
{
    public static TimeSpan Interval { get; } = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Off the startup path: the host should not wait on nomercy.tv.
        await Task.Yield();

        using PeriodicTimer timer = new(Interval);

        do
        {
            await RefreshOnceAsync(stoppingToken);
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task RefreshOnceAsync(CancellationToken ct = default)
    {
        try
        {
            await keys.RefreshAsync(addresses.Keys, ct);
            await revocations.RefreshAsync(addresses.Revocations, ct);

            if (tokens.AccessToken is { Length: > 0 } token)
                await entitlements.RefreshAsync(addresses.Entitlements(), token, ct);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Plugin trust: this refresh did not finish.");
        }
    }
}
