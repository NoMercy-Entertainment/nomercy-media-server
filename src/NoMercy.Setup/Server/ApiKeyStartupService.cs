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
/// but a host rebuilt after setup or a port conflict has its own loader, and the first
/// host's loader stops its retry loop when that host's container is disposed. Without
/// this, a first load that failed (no bearer yet on a first boot, or the API down with
/// no cache) left the rebuilt host without keys, and keys the first host only had from
/// the cache were never refreshed, until a restart (#665). Keys already fetched from the
/// API make <see cref="IApiKeyLoader.LoadKeys"/> return at once; keys from the cache
/// make it start this host's refresh loop.
/// </summary>
public sealed class ApiKeyStartupService(IApiKeyLoader apiKeyLoader) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) =>
        apiKeyLoader.LoadKeys(stoppingToken);
}
