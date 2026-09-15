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

using Newtonsoft.Json;
using NoMercy.NmSystem.Auth;
using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.Information;
using NoMercy.NmSystem.Networking;
using NoMercy.NmSystem.NewtonSoftConverters;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Setup.Auth;

/// <summary>
/// Asks the NoMercy API to connect to one of this server's public addresses from the
/// cloud. The API only ever fetches /status on a name that belongs to this server.
/// </summary>
public class CloudReachabilityProbe(IAuthTokenStore authTokenStore) : IReachabilityProbe
{
    public async Task<ReachabilityVerdict> ProbeAsync(string url, CancellationToken ct)
    {
        string? token = authTokenStore.AccessToken;
        if (string.IsNullOrEmpty(token))
            return ReachabilityVerdict.Unknown;

        try
        {
            GenericHttpClient client = new(ExternalServicesConfig.Current.ApiServerBaseUrl);
            client.SetDefaultHeaders(ExternalServicesConfig.Current.UserAgent, token);

            string response = await client.SendAndReadAsync(
                HttpMethod.Post,
                "probe",
                new FormUrlEncodedContent([
                    new KeyValuePair<string, string>("id", Info.DeviceId.ToString()),
                    new KeyValuePair<string, string>("urls[]", url),
                ])
            );

            ProbeResponse? data = response.FromJson<ProbeResponse>();
            ProbeResult? result = data?.Data?.Results?.FirstOrDefault();

            if (data?.Status != "ok" || result is null)
                return ReachabilityVerdict.Unknown;

            // The API answers "unknown" when it cannot judge — it sits behind the same router
            // as this server, or the name was not one of ours. That is not a closed port.
            return result.Verdict switch
            {
                "reachable" => ReachabilityVerdict.Reachable,
                "unreachable" => ReachabilityVerdict.Unreachable,
                _ => ReachabilityVerdict.Unknown,
            };
        }
        catch (Exception ex)
        {
            Logger.Register(
                $"Reachability probe could not run: {ex.Unwrap()}",
                LogEventLevel.Warning
            );
            return ReachabilityVerdict.Unknown;
        }
    }

    private sealed class ProbeResponse
    {
        [JsonProperty("status")]
        public string? Status { get; set; }

        [JsonProperty("data")]
        public ProbeData? Data { get; set; }
    }

    private sealed class ProbeData
    {
        [JsonProperty("results")]
        public List<ProbeResult>? Results { get; set; }
    }

    private sealed class ProbeResult
    {
        [JsonProperty("url")]
        public string? Url { get; set; }

        [JsonProperty("verdict")]
        public string? Verdict { get; set; }

        [JsonProperty("reachable")]
        public bool Reachable { get; set; }
    }
}
