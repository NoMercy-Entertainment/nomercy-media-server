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
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Data.Security;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Security;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Networking.Discovery;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Security;

/// <summary>
/// fail2ban semantics for the request pipeline: offences accumulate inside a
/// sliding window, and crossing the threshold produces a ban. A known probe is
/// never a mistake, so its ban is permanent. A ban made only of unrouted misses
/// can be a confused client, so it expires and doubles on each repeat.
/// </summary>
public class AbuseGuard(
    IIpBanRepository repository,
    IAbuseGuardSettings settings,
    IActivityLogger activityLogger,
    IEventBus eventBus,
    ILogger<AbuseGuard> logger,
    TimeProvider timeProvider,
    IUserCache userCache,
    INetworkDiscovery networkDiscovery
) : IAbuseGuard
{
    private readonly ConcurrentDictionary<string, List<Offence>> _offences = new();

    // Bans are cached so an already-banned caller costs one dictionary lookup
    // instead of a database round trip on every request it fires at us.
    private readonly ConcurrentDictionary<string, DateTime> _bans = new();

    private readonly record struct Offence(DateTime At, int Weight, ProbeVerdict Verdict);

    public Task<bool> IsBannedAsync(IPAddress? address, CancellationToken ct)
    {
        if (address is null || !settings.Enabled || IsOwnExternalAddress(address))
            return Task.FromResult(false);

        string key = address.ToString();
        if (!_bans.TryGetValue(key, out DateTime expiresAt))
            return Task.FromResult(false);

        if (expiresAt > timeProvider.GetUtcNow().UtcDateTime)
            return Task.FromResult(true);

        _bans.TryRemove(key, out _);
        return Task.FromResult(false);
    }

    public async Task RecordAsync(IPAddress? address, RequestOutcome outcome, CancellationToken ct)
    {
        if (address is null || !settings.Enabled || IsAllowlisted(address))
            return;

        ProbeVerdict verdict = ProbeDetector.Classify(outcome);
        int weight = ProbeDetector.Weight(verdict);
        if (weight == 0)
            return;

        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        DateTime windowStart = now - settings.Window;
        string key = address.ToString();

        int score;
        int count;
        bool knownProbe;
        List<Offence> window = _offences.GetOrAdd(key, _ => []);
        lock (window)
        {
            window.RemoveAll(offence => offence.At <= windowStart);
            window.Add(new(now, weight, verdict));
            score = window.Sum(offence => offence.Weight);
            count = window.Count;
            knownProbe = window.Any(offence => offence.Verdict == ProbeVerdict.KnownProbe);
        }

        if (score < settings.MaxScore)
            return;

        _offences.TryRemove(key, out _);
        await BanForProbeAsync(
            address,
            outcome.Path,
            count,
            knownProbe ? ProbeVerdict.KnownProbe : ProbeVerdict.Suspicious,
            ct
        );
    }

    public Task<IpBan> BanAsync(
        IPAddress address,
        string reason,
        TimeSpan duration,
        bool manual,
        CancellationToken ct
    )
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        TimeSpan capped = duration > settings.MaxBanDuration ? settings.MaxBanDuration : duration;

        return PersistAsync(
            address,
            reason,
            path: null,
            offenceCount: 0,
            banNumber: 1,
            manual: manual,
            bannedAt: now,
            expiresAt: now + capped,
            ct
        );
    }

    public async Task<bool> UnbanAsync(string address, CancellationToken ct)
    {
        _bans.TryRemove(address, out _);
        _offences.TryRemove(address, out _);

        bool removed = await repository.RemoveAsync(address, ct);
        if (!removed)
            return false;

        await activityLogger.LogSystemAsync(
            ActivityCategory.Security,
            "security.ip_unbanned",
            metadata: new { address },
            ct: ct
        );

        return true;
    }

    public Task<List<IpBan>> ActiveBansAsync(CancellationToken ct) =>
        repository.ActiveAsync(timeProvider.GetUtcNow().UtcDateTime, ct);

    public async Task RefreshAsync(CancellationToken ct)
    {
        List<IpBan> active = await repository.ActiveAsync(timeProvider.GetUtcNow().UtcDateTime, ct);

        _bans.Clear();
        foreach (IpBan ban in active)
            _bans[ban.Address] = ban.ExpiresAt;
    }

    private bool IsAllowlisted(IPAddress address)
    {
        if (ClientIpResolver.IsPrivateNetwork(address) || IsOwnExternalAddress(address))
            return true;

        foreach (IpRange range in settings.Allowlist)
            if (range.Contains(address))
                return true;

        return false;
    }

    // Home devices reach the server through the tunnel with the server's own
    // public address. Read on every call, so a changed address applies at once.
    private bool IsOwnExternalAddress(IPAddress address)
    {
        IPAddress candidate = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return Matches(networkDiscovery.ExternalIp, candidate)
            || Matches(networkDiscovery.ExternalIpV6, candidate);
    }

    private static bool Matches(string? own, IPAddress candidate) =>
        IPAddress.TryParse(own, out IPAddress? parsed)
        && !parsed.Equals(IPAddress.Any)
        && parsed.Equals(candidate);

    private async Task<IpBan> BanForProbeAsync(
        IPAddress address,
        string path,
        int offenceCount,
        ProbeVerdict verdict,
        CancellationToken ct
    )
    {
        DateTime now = timeProvider.GetUtcNow().UtcDateTime;
        int priorBans = await repository.PriorBanCountAsync(address.ToString(), ct);

        // A known probe is malicious, so only a moderator ends its ban. A ban of
        // unrouted misses alone doubles on each repeat, up to the maximum.
        DateTime expiresAt = DateTime.MaxValue;
        if (verdict != ProbeVerdict.KnownProbe)
        {
            TimeSpan duration = settings.BanDuration * Math.Pow(2, Math.Min(priorBans, 16));
            expiresAt =
                now + (duration > settings.MaxBanDuration ? settings.MaxBanDuration : duration);
        }

        IpBan ban = await PersistAsync(
            address,
            reason: verdict.ToString(),
            path: path,
            offenceCount: offenceCount,
            banNumber: priorBans + 1,
            manual: false,
            bannedAt: now,
            expiresAt: expiresAt,
            ct
        );

        await NotifyOwnersAsync(address, verdict, path, offenceCount, expiresAt, ct);

        return ban;
    }

    private async Task NotifyOwnersAsync(
        IPAddress address,
        ProbeVerdict verdict,
        string path,
        int offenceCount,
        DateTime expiresAt,
        CancellationToken ct
    )
    {
        string sentence =
            expiresAt == DateTime.MaxValue
                ? "permanently banned"
                : $"banned until {expiresAt.ToString("u", CultureInfo.InvariantCulture)}";

        foreach (User owner in userCache.Users.Where(user => user.Owner))
        {
            await eventBus.PublishAsync(
                new UserNotifiedEvent
                {
                    Title = "IP banned",
                    Message =
                        $"{address} was {sentence} as a {verdict} after {offenceCount} offence(s) on {path}.",
                    Type = "security",
                    Route = "/settings/security/bans",
                    UserId = owner.Id,
                },
                ct
            );
        }
    }

    private async Task<IpBan> PersistAsync(
        IPAddress address,
        string reason,
        string? path,
        int offenceCount,
        int banNumber,
        bool manual,
        DateTime bannedAt,
        DateTime expiresAt,
        CancellationToken ct
    )
    {
        IpBan ban = new()
        {
            Address = address.ToString(),
            Reason = reason,
            LastPath = path,
            OffenceCount = offenceCount,
            BanNumber = banNumber,
            Manual = manual,
            BannedAt = bannedAt,
            ExpiresAt = expiresAt,
        };

        IpBan stored = await repository.UpsertAsync(ban, ct);
        _bans[stored.Address] = stored.ExpiresAt;

        logger.LogWarning(
            "Banned {Address} until {ExpiresAt} ({Reason}, last path {Path})",
            stored.Address,
            stored.ExpiresAt,
            stored.Reason,
            stored.LastPath
        );

        await activityLogger.LogSystemAsync(
            ActivityCategory.Security,
            "security.ip_banned",
            metadata: new
            {
                address = stored.Address,
                reason = stored.Reason,
                path = stored.LastPath,
                offences = stored.OffenceCount,
                expires_at = stored.ExpiresAt,
                manual = stored.Manual,
            },
            ct: ct
        );

        return stored;
    }
}
