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

using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Auth;

namespace NoMercy.Plugins.Telemetry;

/// <summary>
/// Posts a report to NoMercy and forgets it if the line is down.
/// <para>
/// Dropped rather than queued. A queue of reports nobody could send is a
/// server storing a month of counters to deliver an hour that has long stopped
/// meaning anything, and a self-hosted server offline for a week should come
/// back light rather than loud.
/// </para>
/// </summary>
public class PluginHttpTelemetrySink(
    HttpClient http,
    IAuthTokenStore tokens,
    ILogger<PluginHttpTelemetrySink> logger
) : IPluginTelemetrySink
{
    public Task SendAsync(PluginTelemetryReport report, CancellationToken ct = default) =>
        PostAsync("plugins/telemetry", report, ct);

    public Task SendInstallAsync(PluginInstallReport report, CancellationToken ct = default) =>
        PostAsync("plugins/installs", report, ct);

    private async Task PostAsync<T>(string path, T body, CancellationToken ct)
    {
        if (tokens.AccessToken is not { Length: > 0 } token)
            return;

        try
        {
            HttpRequestMessage message = new(HttpMethod.Post, path)
            {
                Content = JsonContent.Create(body),
            };

            message.Headers.Authorization = new("Bearer", token);

            using HttpResponseMessage response = await http.SendAsync(message, ct);

            if (!response.IsSuccessStatusCode)
                logger.LogDebug(
                    "Plugin telemetry to {Path} answered {StatusCode}.",
                    path,
                    response.StatusCode
                );
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogDebug(exception, "Plugin telemetry to {Path} did not reach NoMercy.", path);
        }
    }
}
