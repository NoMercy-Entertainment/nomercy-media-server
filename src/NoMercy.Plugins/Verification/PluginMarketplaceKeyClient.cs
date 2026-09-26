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

using Microsoft.Extensions.Logging;

namespace NoMercy.PluginSdk.Verification;

/// <summary>
/// Fetches the published key set and keeps the keys already held when that
/// fails. Warned once: a server that is offline for a month should not write
/// the same line every few hours.
/// </summary>
public class PluginMarketplaceKeyClient(
    HttpClient http,
    PluginMarketplaceKeys keys,
    ILogger<PluginMarketplaceKeyClient> logger
)
{
    private bool _warned;

    public async Task RefreshAsync(Uri address, CancellationToken ct = default)
    {
        try
        {
            string body = await http.GetStringAsync(address, ct);

            if (keys.Apply(body))
                return;

            Failed(null, "the answer was not a key set");
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Failed(exception, exception.Message);
        }
    }

    private void Failed(Exception? exception, string reason)
    {
        if (_warned)
        {
            logger.LogDebug(exception, "Plugin keys: the refresh failed again: {Reason}.", reason);
            return;
        }

        _warned = true;
        logger.LogWarning(
            exception,
            "Plugin keys: the refresh failed ({Reason}), so the keys already on this server were kept.",
            reason
        );
    }
}
