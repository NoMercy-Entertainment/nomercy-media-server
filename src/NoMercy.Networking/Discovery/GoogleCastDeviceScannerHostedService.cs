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

namespace NoMercy.Networking.Discovery;

/// <summary>
/// Parallel to <see cref="MdnsDeviceScannerHostedService"/> — same 30s
/// probe-and-retry cadence, kept as its own hosted service rather than folded
/// into the existing one so the working, tested NoMercy-fingerprint scanner
/// stays untouched.
/// </summary>
public sealed class GoogleCastDeviceScannerHostedService(
    GoogleCastDeviceScanner scanner,
    ILogger<GoogleCastDeviceScannerHostedService> logger
) : BackgroundService
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(30);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        scanner.Start(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                scanner.Probe();
                await scanner.ProbeHostsAsync(stoppingToken);
                await Task.Delay(ProbeInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Same tolerance as the NoMercy-fingerprint scanner: mDNS
                // multicast can flake when interfaces flip (VPN connect,
                // hotspot toggle, NIC sleep). Don't let a probe failure tear
                // down the host — log and retry on the next interval.
                logger.LogWarning(ex, "Google Cast mDNS probe failed; will retry");
                try
                {
                    await Task.Delay(ProbeInterval, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
}
