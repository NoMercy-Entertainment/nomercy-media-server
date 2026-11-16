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

namespace NoMercy.Setup.Server;

/// <summary>
/// Loads the API keys when a host starts. Boot loads them before the first host runs,
/// but a host rebuilt after setup or a port conflict has its own loader, and the
/// first host's retry loop stops with that host. Without this, a first load that
/// failed (no bearer yet on a first boot, or the API down with no cache) left the
/// rebuilt host without keys until a restart (#665). When the keys are already
/// loaded, <see cref="IApiKeyLoader.LoadKeys"/> returns at once.
/// </summary>
public sealed class ApiKeyStartupService(IApiKeyLoader apiKeyLoader) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        apiKeyLoader.LoadKeys(stoppingToken);
}
