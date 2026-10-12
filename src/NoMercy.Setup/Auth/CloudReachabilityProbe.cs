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

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
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
/// Asks the NoMercy API to call this server back. The API calls the address this request
/// arrives from, never an address the server names, so the call goes out once over IPv4
/// and once over IPv6 to learn each family's answer.
/// </summary>
public class CloudReachabilityProbe(IAuthTokenStore authTokenStore) : IReachabilityProbe
{
    public async Task<ReachabilityVerdict> ProbeBackAsync(
        AddressFamily family,
        int port,
        string code,
        CancellationToken ct
    )
    {
        string? token = authTokenStore.AccessToken;
        if (string.IsNullOrEmpty(token))
            return ReachabilityVerdict.Unknown;

        try
        {
            using HttpClient client = CreateClient(family);
            using HttpRequestMessage request = new(
                HttpMethod.Post,
                new Uri(new Uri(ExternalServicesConfig.Current.ApiServerBaseUrl), "probe")
            );
            request.Headers.Authorization = new("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.TryAddWithoutValidation(
                "User-Agent",
                ExternalServicesConfig.Current.UserAgent
            );
            request.Content = new FormUrlEncodedContent([
                new KeyValuePair<string, string>("id", Info.DeviceId.ToString()),
                new KeyValuePair<string, string>("code", code),
                new KeyValuePair<string, string>(
                    "port",
                    port.ToString(CultureInfo.InvariantCulture)
                ),
            ]);

            using HttpResponseMessage response = await client.SendAsync(request, ct);

            // An API from before the call-back existed refuses the request (it wants urls).
            // That is a check that did not run, not a closed port.
            if (!response.IsSuccessStatusCode)
                return ReachabilityVerdict.Unknown;

            return ParseVerdict(await response.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex) when (IsMissingFamily(ex))
        {
            // This host has no route for the family (no IPv6 at home is common). Nothing
            // was asked, so nothing is known.
            return ReachabilityVerdict.Unknown;
        }
        catch (Exception ex)
        {
            Logger.Register(
                $"Reachability call-back over {family} could not run: {ex.Unwrap()}",
                LogEventLevel.Warning
            );
            return ReachabilityVerdict.Unknown;
        }
    }

    internal static ReachabilityVerdict ParseVerdict(string json)
    {
        ProbeResponse? data = json.FromJson<ProbeResponse>();
        ProbeResult? result = data?.Data?.Results?.FirstOrDefault();

        if (data?.Status != "ok" || result is null)
            return ReachabilityVerdict.Unknown;

        // The API answers "unknown" when it cannot judge — it sits behind the same router
        // as this server, or it cannot make calls on this family itself. That is not a
        // closed port.
        return result.Verdict switch
        {
            "reachable" => ReachabilityVerdict.Reachable,
            "unreachable" => ReachabilityVerdict.Unreachable,
            _ => ReachabilityVerdict.Unknown,
        };
    }

    /// <summary>
    /// A client whose connections only ever use <paramref name="family"/>, so the API sees
    /// the server's public address for exactly that family.
    /// </summary>
    internal static HttpClient CreateClient(AddressFamily family) =>
        new(
            new SocketsHttpHandler
            {
                ConnectTimeout = TimeSpan.FromSeconds(5),
                ConnectCallback = async (context, token) =>
                {
                    IPAddress[] addresses = await Dns.GetHostAddressesAsync(
                        context.DnsEndPoint.Host,
                        family,
                        token
                    );
                    if (addresses.Length == 0)
                        throw new SocketException((int)SocketError.AddressFamilyNotSupported);

                    Socket socket = new(family, SocketType.Stream, ProtocolType.Tcp)
                    {
                        NoDelay = true,
                    };
                    try
                    {
                        await socket.ConnectAsync(addresses, context.DnsEndPoint.Port, token);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                },
            }
        )
        {
            Timeout = TimeSpan.FromSeconds(15),
        };

    private static bool IsMissingFamily(Exception ex)
    {
        for (Exception? e = ex; e is not null; e = e.InnerException)
        {
            if (
                e is SocketException
                {
                    SocketErrorCode: SocketError.AddressFamilyNotSupported
                        or SocketError.NetworkUnreachable
                        or SocketError.HostUnreachable
                        or SocketError.HostNotFound
                        or SocketError.NoData
                        or SocketError.AddressNotAvailable
                }
            )
                return true;
        }

        return false;
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
        [JsonProperty("verdict")]
        public string? Verdict { get; set; }
    }
}
