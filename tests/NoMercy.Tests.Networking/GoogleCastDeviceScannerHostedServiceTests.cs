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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Networking.Discovery;
using Xunit;

namespace NoMercy.Tests.Networking;

/// <summary>
/// REQUIREMENT: the hosted-service wrapper must start the scanner and stop
/// cleanly on a cancelled token without throwing — the 30s probe-and-retry
/// loop body itself needs a real multicast join succeeding and is itemized
/// (see MdnsDeviceScannerHostedServiceTests, same reasoning) since it depends
/// on live LAN multicast, not something a sandboxed unit test can assert on
/// deterministically.
/// </summary>
[Trait("Category", "Unit")]
public sealed class GoogleCastDeviceScannerHostedServiceTests
{
    [Fact]
    public async Task StartAsync_WithAlreadyCancelledToken_StopsImmediately_WithoutThrowing()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );
        GoogleCastDeviceScannerHostedService hostedService = new(
            scanner,
            NullLogger<GoogleCastDeviceScannerHostedService>.Instance
        );
        using CancellationTokenSource cts = new();
        cts.Cancel();

        Exception? ex = await Record.ExceptionAsync(() => hostedService.StartAsync(cts.Token));

        Assert.Null(ex);

        scanner.Dispose();
    }

    [Fact]
    public async Task StartAsync_ThenImmediateStop_DoesNotThrow()
    {
        GoogleCastDeviceScanner scanner = new(
            NullLogger<GoogleCastDeviceScanner>.Instance,
            new NonBindingMulticastTransport()
        );
        GoogleCastDeviceScannerHostedService hostedService = new(
            scanner,
            NullLogger<GoogleCastDeviceScannerHostedService>.Instance
        );
        using CancellationTokenSource cts = new();

        Task startTask = hostedService.StartAsync(cts.Token);
        await cts.CancelAsync();
        Exception? ex = await Record.ExceptionAsync(() => hostedService.StopAsync(cts.Token));

        Assert.Null(ex);

        await Task.WhenAny(startTask, Task.Delay(1000));
        scanner.Dispose();
    }
}
