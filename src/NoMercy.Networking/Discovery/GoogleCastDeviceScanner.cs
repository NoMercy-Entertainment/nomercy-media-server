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

using System.Collections.Concurrent;
using Makaretu.Dns;
using Microsoft.Extensions.Logging;
using Message = Makaretu.Dns.Message;

namespace NoMercy.Networking.Discovery;

/// <summary>
/// Listens for the OS-level Google Cast mDNS announcement (`_googlecast._tcp`),
/// which every Chromecast, Cast-Connect-enabled Android TV, and Google Home/Nest
/// speaker on the LAN broadcasts regardless of what app (if any) is in the
/// foreground. Deliberately separate from <see cref="MdnsDeviceScanner"/>,
/// which only listens for NoMercy's own proprietary `_nomercy._tcp`
/// announcement — that one goes silent the moment the NoMercy app process
/// stops, which is exactly the gap this scanner closes: a registered device
/// sitting idle on another app (e.g. Spotify) still answers this query, so
/// its physical presence can still be reported even though it dropped off
/// the websocket bus.
///
/// Held as an in-memory, TTL-based snapshot rather than written to the
/// Devices table — a real Chromecast's mDNS record carries no NoMercy
/// fingerprint, so most hits will never correspond to a row at all, and
/// persisting transient LAN presence for devices we may never own would be
/// both unnecessary and a migration this slice doesn't need.
/// </summary>
public sealed class GoogleCastDeviceScanner : ICastMdnsRegistry, IDisposable
{
    public const string ServiceType = "_googlecast._tcp";

    // Matches MdnsDeviceScanner's own 5-minute staleness convention. Pruned
    // lazily on read rather than swept on a timer — a LAN's Cast device count
    // is small enough that a linear scan of live entries is cheap.
    private static readonly TimeSpan StalenessWindow = TimeSpan.FromMinutes(5);

    private readonly ILogger<GoogleCastDeviceScanner> _logger;
    private readonly IMdnsMulticastTransport _multicast;
    private readonly ServiceDiscovery _discovery;

    // Keyed by Google's own Cast device id (from the `id=` TXT key) rather
    // than IP, since IP is what callers look up BY — this dedupes repeat
    // announcements from the same physical device.
    private readonly ConcurrentDictionary<
        string,
        (string? FriendlyName, string? Model, string Ip, int? Port, DateTime SeenAt)
    > _seen = new();

    private int _started;

    // The transport is optional so DI keeps building this singleton without a
    // registration; a test passes one that never joins the 5353 group.
    public GoogleCastDeviceScanner(
        ILogger<GoogleCastDeviceScanner> logger,
        IMdnsMulticastTransport? multicast = null
    )
    {
        _logger = logger;
        _multicast = multicast ?? new MdnsMulticastTransport();
        _discovery = new(_multicast.Service);
    }

    public void Start(CancellationToken stoppingToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        _discovery.ServiceInstanceDiscovered += OnInstanceDiscovered;
        _multicast.Service.NetworkInterfaceDiscovered += (_, _) =>
            _discovery.QueryServiceInstances(ServiceType);

        _multicast.Start();

        stoppingToken.Register(() =>
        {
            _multicast.Stop();
            _discovery.ServiceInstanceDiscovered -= OnInstanceDiscovered;
        });
    }

    public void Probe() => _discovery.QueryServiceInstances(ServiceType);

    /// <summary>
    /// True when a Google Cast mDNS announcement was seen, within the
    /// staleness window, from an IP matching a registered NoMercy device's
    /// own <c>LanIp</c> — written by <see cref="MdnsDeviceScanner"/> from
    /// that same physical box's separate `_nomercy._tcp` announcement. Both
    /// come from the same NIC, so IP is the join key; a real Chromecast's
    /// mDNS record carries no NoMercy fingerprint to match on instead.
    /// </summary>
    public bool IsReachable(string? lanIp)
    {
        if (string.IsNullOrEmpty(lanIp))
            return false;

        DateTime cutoff = DateTime.UtcNow - StalenessWindow;
        return _seen.Values.Any(v => v.Ip == lanIp && v.SeenAt >= cutoff);
    }

    public IReadOnlyCollection<CastMdnsHit> GetSeen()
    {
        DateTime cutoff = DateTime.UtcNow - StalenessWindow;
        return
        [
            .. _seen
                .Where(kv => kv.Value.SeenAt >= cutoff)
                .Select(kv => new CastMdnsHit(
                    kv.Key,
                    kv.Value.FriendlyName,
                    kv.Value.Model,
                    kv.Value.Ip,
                    kv.Value.SeenAt
                )),
        ];
    }

    private void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            (string? id, string? friendlyName, string? model) = ExtractCastInfo(e.Message);
            if (string.IsNullOrEmpty(id))
                return;

            (string? ip, int? port) = MdnsDeviceScanner.ExtractEndpoint(e.Message);
            if (ip is null)
                return;

            _seen[id] = (friendlyName, model, ip, port, DateTime.UtcNow);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Google Cast mDNS hit processing failed");
        }
    }

    // Internal so the pure TXT parsing is directly unit-testable against real
    // Makaretu.Dns message objects — no multicast socket needed, same
    // reasoning as MdnsDeviceScanner.ExtractFingerprint. SRV/A endpoint
    // extraction is reused from MdnsDeviceScanner rather than duplicated,
    // since both scanners' endpoint records follow the same shape.
    internal static (string? Id, string? FriendlyName, string? Model) ExtractCastInfo(Message msg)
    {
        string? id = null;
        string? friendlyName = null;
        string? model = null;

        foreach (TXTRecord rec in msg.AdditionalRecords.OfType<TXTRecord>())
        {
            foreach (string s in rec.Strings)
            {
                if (id is null && s.StartsWith("id=", StringComparison.OrdinalIgnoreCase))
                    id = s[3..];
                else if (
                    friendlyName is null
                    && s.StartsWith("fn=", StringComparison.OrdinalIgnoreCase)
                )
                    friendlyName = s[3..];
                else if (model is null && s.StartsWith("md=", StringComparison.OrdinalIgnoreCase))
                    model = s[3..];
            }
        }

        return (id, friendlyName, model);
    }

    public void Dispose()
    {
        // Defensive: unsubscribe regardless of whether the stopping token fired,
        // so the handler is not left registered on a disposed discovery object.
        _discovery.ServiceInstanceDiscovered -= OnInstanceDiscovered;
        _multicast.Dispose();
        _discovery.Dispose();
    }
}
