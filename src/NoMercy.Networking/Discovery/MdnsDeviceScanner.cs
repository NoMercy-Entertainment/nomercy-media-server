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

using Makaretu.Dns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.Database.Models.Users;
using NoMercy.Networking.Devices;
using Message = Makaretu.Dns.Message;

namespace NoMercy.Networking.Discovery;

public sealed class MdnsDeviceScanner : IDisposable
{
    public const string ServiceType = "_nomercy._tcp";

    private readonly IDbContextFactory<MediaContext> _contextFactory;
    private readonly ILogger<MdnsDeviceScanner> _logger;
    private readonly IDeviceListChangeNotifier? _changeNotifier;
    private readonly IMdnsMulticastTransport _multicast;
    private readonly ServiceDiscovery _discovery;
    private int _started;

    // The transport is optional so DI keeps building this singleton without a
    // registration; a test passes one that never joins the 5353 group.
    public MdnsDeviceScanner(
        IDbContextFactory<MediaContext> contextFactory,
        ILogger<MdnsDeviceScanner> logger,
        IDeviceListChangeNotifier? changeNotifier = null,
        IMdnsMulticastTransport? multicast = null
    )
    {
        _contextFactory = contextFactory;
        _logger = logger;
        _changeNotifier = changeNotifier;
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

    private async void OnInstanceDiscovered(object? sender, ServiceInstanceDiscoveryEventArgs e)
    {
        try
        {
            string? fingerprint = ExtractFingerprint(e.Message);
            if (string.IsNullOrEmpty(fingerprint))
                return;

            (string? ip, int? port) = ExtractEndpoint(e.Message);
            if (ip is null)
                return;

            await using MediaContext ctx = await _contextFactory.CreateDbContextAsync();
            Device? device = await ctx.Devices.FirstOrDefaultAsync(d =>
                d.Fingerprint == fingerprint
            );

            if (device is null)
                return;

            DateTime now = DateTime.UtcNow;
            bool ipChanged = device.LanIp != ip;
            bool portChanged = device.LanPort != port;
            bool stale =
                device.MdnsSeenAt is null
                || now - device.MdnsSeenAt.Value > TimeSpan.FromMinutes(5);

            if (!ipChanged && !portChanged && !stale)
                return;

            device.LanIp = ip;
            device.LanPort = port;
            device.MdnsSeenAt = now;
            await ctx.SaveChangesAsync();

            if (_changeNotifier is not null && device.OwnerUserId is not null)
                await _changeNotifier.BroadcastChange(device.OwnerUserId.Value);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "mDNS hit processing failed");
        }
    }

    // Internal so the pure TXT/SRV/A record parsing is directly unit-testable
    // against real Makaretu.Dns message objects — no multicast socket needed.
    internal static string? ExtractFingerprint(Message msg)
    {
        foreach (TXTRecord rec in msg.AdditionalRecords.OfType<TXTRecord>())
        {
            foreach (string s in rec.Strings)
            {
                if (s.StartsWith("fp=", StringComparison.OrdinalIgnoreCase))
                    return s[3..];
            }
        }

        return null;
    }

    internal static (string?, int?) ExtractEndpoint(Message msg)
    {
        SRVRecord? srv = msg.AdditionalRecords.OfType<SRVRecord>().FirstOrDefault();
        if (srv is null)
            return (null, null);

        ARecord? a = msg.AdditionalRecords.OfType<ARecord>().FirstOrDefault();
        return (a?.Address.ToString(), srv.Port);
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
