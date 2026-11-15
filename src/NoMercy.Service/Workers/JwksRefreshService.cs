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

using NoMercy.NmSystem.Configuration;
using NoMercy.Setup.Auth;

namespace NoMercy.Service.Workers;

// Keeps the offline realm signing-key cache fresh so a restart during an outage can validate tokens.
public sealed class JwksRefreshService(IHttpClientFactory httpClientFactory) : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(12);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Boot already refreshed once; wait a full interval before the first timed refresh.
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(RefreshInterval, stoppingToken);

                HttpClient client = httpClientFactory.CreateClient(
                    ExternalServicesConfig.KeycloakHttpClientName
                );
                await OfflineJwksCache.RefreshAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Host is shutting down — exit cleanly.
        }
    }
}
