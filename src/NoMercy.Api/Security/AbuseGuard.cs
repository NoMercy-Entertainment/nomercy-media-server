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
using System.Net;
using Microsoft.Extensions.Logging;
using NoMercy.Authorization;
using NoMercy.Data.Security;
using NoMercy.Database.Activity;
using NoMercy.Database.Models.Security;
using NoMercy.Database.Models.Users;
using NoMercy.Events;
using NoMercy.Events.Media;
using NoMercy.Networking.Http;

namespace NoMercy.Api.Security;

/// <summary>
/// fail2ban semantics for the request pipeline: offences accumulate inside a
/// sliding window, and crossing the threshold produces a ban. A scanner is
/// never a mistake, so a probe-triggered ban never expires on its own — only
/// a moderator lifting it through UnbanAsync ends it.
/// </summary>
public class AbuseGuard(
    IIpBanRepository repository,
    IAbuseGuardSettings settings,
    IActivityLogger activityLogger,
    IEventBus eventBus,
    ILogger<AbuseGuard> logger,
    TimeProvider timeProvider,
    IUserCache userCache
) : IAbuseGuard
{
    private readonly ConcurrentDictionary<string, List<Offence>> _offences = new();

    // Bans are cached so an already-banned caller costs one dictionary lookup
    // instead of a database round trip on every request it fires at us.
    private readonly ConcurrentDictionary<string, DateTime> _bans = new();

    private readonly record struct Offence(DateTime At, int Weight);

    public Task<bool> IsBannedAsync(IPAddress? address, CancellationToken ct)
    {
        if (address is null || !settings.Enabled)
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
        List<Offence> window = _offences.GetOrAdd(key, _ => []);
        lock (window)
        {
            window.RemoveAll(offence => offence.At <= windowStart);
            window.Add(new(now, weight));
            score = window.Sum(offence => offence.Weight);
            count = window.Count;
        }

        if (score < settings.MaxScore)
            return;

        _offences.TryRemove(key, out _);
        await BanForProbeAsync(address, outcome.Path, count, verdict, ct);
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
        if (ClientIpResolver.IsPrivateNetwork(address))
            return true;

        foreach (IpRange range in settings.Allowlist)
            if (range.Contains(address))
                return true;

        return false;
    }

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

        // A caller that tripped the probe detector is presumed malicious, not
        // mistaken, so the ban does not expire on a timer — only a moderator
        // lifting it through UnbanAsync ends it.
        IpBan ban = await PersistAsync(
            address,
            reason: verdict.ToString(),
            path: path,
            offenceCount: offenceCount,
            banNumber: priorBans + 1,
            manual: false,
            bannedAt: now,
            expiresAt: DateTime.MaxValue,
            ct
        );

        await NotifyOwnersAsync(address, verdict, path, offenceCount, ct);

        return ban;
    }

    private async Task NotifyOwnersAsync(
        IPAddress address,
        ProbeVerdict verdict,
        string path,
        int offenceCount,
        CancellationToken ct
    )
    {
        foreach (User owner in userCache.Users.Where(user => user.Owner))
        {
            await eventBus.PublishAsync(
                new UserNotifiedEvent
                {
                    Title = "IP banned",
                    Message =
                        $"{address} was permanently banned as a {verdict} after {offenceCount} offence(s) on {path}.",
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
