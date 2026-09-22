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
using NoMercy.PluginSdk.Access;
using NoMercy.PluginSdk.Telemetry;

namespace NoMercy.PluginSdk;

/// <summary>
/// Brings the platform's event listeners into being.
/// <para>
/// A listener subscribes when it is built, and a registration nothing resolves
/// is never built. Asking for them here is what makes them exist, and it is
/// one place rather than a line in whatever happened to start first.
/// </para>
/// </summary>
public class PluginListenerService(PluginCrashListener crashes, PluginAccessChangedListener access)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken)
    {
        crashes.Dispose();
        access.Dispose();

        return Task.CompletedTask;
    }
}
