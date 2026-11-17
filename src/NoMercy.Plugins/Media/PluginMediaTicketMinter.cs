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
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Media;

/// <summary>
/// The host mints every media link a plugin hands out.
/// <para>
/// A plugin gives the server an upstream and gets back a ticket naming the
/// plugin, the one account it is for, and a deadline. The upstream itself
/// never crosses the wire: the ticket carries a hash of it and the server
/// keeps the address. A link a viewer can read is a link a viewer can share,
/// and an upstream that carries a credential would then be shared with it.
/// </para>
/// <para>
/// The deadline is for starting playback, not for continuing it. A live HLS
/// player fetches the same playlist every few seconds for as long as the
/// viewer watches, so a deadline checked on every request ended every live
/// channel at the same minute. A ticket that is played stays good while it
/// keeps being played (<see cref="IdleTimeout" />), up to
/// <see cref="MaxLifetime" />. One that nobody plays still runs out at its
/// deadline.
/// </para>
/// </summary>
public class PluginMediaTicketMinter(TimeProvider clock, byte[] serverKey)
{
    private static readonly Uri FileTicketAddress = new("file:///plugin-data");

    private readonly ConcurrentDictionary<string, Held> _upstreams = new();

    /// <summary>
    /// The tickets each holder (one account, or one LAN device, on one plugin)
    /// was handed, oldest first. What the per-holder cap counts and what the
    /// sweep prunes; guarded by <see cref="_gate" />.
    /// </summary>
    private readonly Dictionary<(Ulid PluginId, string Holder), List<string>> _held = new();

    private readonly Lock _gate = new();

    private DateTimeOffset _lastSweep = DateTimeOffset.MinValue;

    private int _mintsSinceSweep;

    /// <summary>
    /// The most live tickets one holder may have on one plugin at a time.
    /// <para>
    /// Every playlist poll mints a ticket per line, and a ticket nobody plays
    /// still lives <c>TicketLifetime</c> (15 minutes). A live window of 10 to
    /// 20 segments polled every 2 seconds is about 4,500 to 9,000 over those
    /// 15 minutes, and a two-hour catch-up window of 6-second segments is
    /// 1,200 lines per poll. Ten thousand holds that, a few megabytes per
    /// holder, and a device or account that goes past it has old, never
    /// played tickets dropped first (see <see cref="EvictionBatch" />).
    /// </para>
    /// </summary>
    public const int MaxLiveTicketsPerHolder = 10_000;

    /// <summary>
    /// How many never-played tickets go at once when a holder reaches the cap,
    /// so a holder that sits at the cap does not pay for a full pass on every
    /// mint.
    /// </summary>
    public const int EvictionBatch = MaxLiveTicketsPerHolder / 10;

    /// <summary>
    /// How often tickets that can no longer play are swept, at the latest:
    /// every this many mints, or the first mint after <see cref="SweepInterval" />.
    /// A sweep on every mint walked the whole map each time, which is
    /// quadratic for a playlist that mints a ticket per line.
    /// </summary>
    public const int SweepEvery = 1024;

    public static TimeSpan SweepInterval { get; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// How long a ticket that has been played stays good after its last
    /// request. A live player asks every 2 to 10 seconds, so a minute or two
    /// of silence means the viewer stopped.
    /// </summary>
    public static TimeSpan IdleTimeout { get; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The most any ticket lives from when it was minted, however busy. A
    /// link that leaked stops working within a day even if it is being used.
    /// </summary>
    public static TimeSpan MaxLifetime { get; } = TimeSpan.FromHours(24);

    /// <summary>One address, which is what most plugins hand over.</summary>
    public string Mint(Ulid pluginId, Guid userId, string upstream, TimeSpan lifetime) =>
        Mint(
            pluginId,
            userId,
            new PluginProxyRequest { Links = [new() { Url = new(upstream) }] },
            lifetime
        );

    public string Mint(Ulid pluginId, Guid userId, PluginProxyRequest request, TimeSpan lifetime) =>
        Mint(pluginId, userId, null, request, null, lifetime);

    /// <summary>
    /// A ticket for a device on the owner's network rather than an account,
    /// minted while a LAN route is serving that device. It plays only on the
    /// LAN media route, for this device, and is forgotten the moment the owner
    /// revokes the device. Same lifetime rules as a user's ticket.
    /// </summary>
    public string MintForDevice(
        Ulid pluginId,
        string deviceId,
        PluginProxyRequest request,
        TimeSpan lifetime
    ) => Mint(pluginId, Guid.Empty, deviceId, request, null, lifetime);

    /// <summary>
    /// A file in the plugin's own folder, by its path relative to that folder.
    /// The path is what is kept, not where it resolved to: it goes through the
    /// folder's guard again on every request, so a file swapped for a link
    /// after minting is still refused.
    /// </summary>
    public string MintFile(Ulid pluginId, Guid userId, string relativePath, TimeSpan lifetime) =>
        Mint(pluginId, userId, null, FileRequest(), relativePath, lifetime);

    /// <summary>A file in the plugin's own folder, for a device rather than an account.</summary>
    public string MintFileForDevice(
        Ulid pluginId,
        string deviceId,
        string relativePath,
        TimeSpan lifetime
    ) => Mint(pluginId, Guid.Empty, deviceId, FileRequest(), relativePath, lifetime);

    // Never fetched: a file ticket is served from the folder by its path. A
    // fixed address keeps any file name, "#" and "%" included, from having to
    // make a valid URI.
    private static PluginProxyRequest FileRequest() =>
        new() { Links = [new() { Url = FileTicketAddress }], RewriteHlsPlaylists = false };

    private string Mint(
        Ulid pluginId,
        Guid userId,
        string? deviceId,
        PluginProxyRequest request,
        string? file,
        TimeSpan lifetime
    )
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(lifetime);
        DateTimeOffset hardStop = now.Add(MaxLifetime);

        // A fresh handle per ticket rather than a hash of the address. Two
        // viewers opening the same stream get their own ticket, and one
        // running out does not take the other's address with it.
        string digest = Ulid.NewUlid().ToString();

        (Ulid, string) holder = (
            pluginId,
            deviceId is null ? $"user:{userId}" : $"device:{deviceId}"
        );

        lock (_gate)
        {
            SweepIfDue(now);

            if (!_held.TryGetValue(holder, out List<string>? minted))
                _held[holder] = minted = [];

            if (minted.Count >= MaxLiveTicketsPerHolder)
                MakeRoom(minted, now, pluginId);

            _upstreams[digest] = new(
                request,
                file,
                expiresAt,
                hardStop > expiresAt ? hardStop : expiresAt,
                null,
                pluginId,
                deviceId
            );
            minted.Add(digest);
        }

        string payload = JsonSerializer.Serialize(
            new
            {
                p = pluginId.ToString(),
                u = userId.ToString(),
                s = digest,
                e = expiresAt.ToUnixTimeSeconds(),
            }
        );

        string body = Base64Url(Encoding.UTF8.GetBytes(payload));

        return $"{body}.{Base64Url(Signature(body))}";
    }

    public PluginMediaTicket? Read(string ticket) => Open(ticket)?.Ticket;

    /// <summary>
    /// Records that a ticket was just played, which keeps it good for
    /// <see cref="IdleTimeout" /> more. Called once a request is cleared to
    /// play, and never for one that was refused.
    /// </summary>
    public void Played(string ticket)
    {
        DateTimeOffset now = clock.GetUtcNow();

        // A ticket that already ran out stays out. Playing it is not a way
        // back, or a link found in a log would be one request from working.
        if (Open(ticket) is not { } opened || !opened.Held.IsLive(now))
            return;

        // Compare-and-swap: if the entry changed or was forgotten in the
        // meantime, the newer state wins rather than being written over.
        _upstreams.TryUpdate(opened.Digest, opened.Held with { LastPlayed = now }, opened.Held);
    }

    private (PluginMediaTicket Ticket, string Digest, Held Held)? Open(string ticket)
    {
        string[] parts = ticket.Split('.');

        if (parts.Length != 2)
            return null;

        try
        {
            // Fixed time, because a comparison that returns early tells whoever
            // is guessing how much of their guess was right.
            if (
                !CryptographicOperations.FixedTimeEquals(
                    Signature(parts[0]),
                    FromBase64Url(parts[1])
                )
            )
                return null;

            using JsonDocument document = JsonDocument.Parse(FromBase64Url(parts[0]));
            JsonElement root = document.RootElement;
            string digest = root.GetProperty("s").GetString() ?? string.Empty;

            if (!_upstreams.TryGetValue(digest, out Held? held))
                return null;

            return (
                new(
                    Ulid.Parse(root.GetProperty("p").GetString()!),
                    Guid.Parse(root.GetProperty("u").GetString()!),
                    held.Request,
                    DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("e").GetInt64())
                )
                {
                    FilePath = held.File,
                    DeviceId = held.DeviceId,
                },
                digest,
                held
            );
        }
        catch (Exception exception)
            when (exception is JsonException or FormatException or KeyNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Null when the ticket may play. One refusal for a ticket that ran out and
    /// for one this server never minted: telling them apart tells somebody
    /// guessing which half of their guess was wrong.
    /// </summary>
    public PluginRefusal? Refuse(string ticket, Guid? asking = null)
    {
        (PluginMediaTicket Ticket, string Digest, Held Held)? opened = Open(ticket);

        if (opened is not { } open || !open.Held.IsLive(clock.GetUtcNow()))
            return new(
                PluginRefusalCodes.MediaTicketExpired,
                opened?.Ticket.PluginId.ToString() ?? "unknown",
                "The media link did not play.",
                "The link has run out. This server mints a new one for every playback, so they are short-lived by design.",
                "Open the item again from the plugin's page.",
                PluginRefusalSeverity.Blocked
            );

        // A device's ticket never plays for an account, whoever asks: the
        // device was given it, not a person, and it is served on the LAN only.
        if (open.Ticket.DeviceId is not null)
            return new(
                PluginRefusalCodes.MediaTicketUserMismatch,
                open.Ticket.PluginId.ToString(),
                "The media link did not play.",
                "The link was minted for a device on the owner's network, not for an account.",
                "Open the item from your own account.",
                PluginRefusalSeverity.Blocked
            );

        if (asking is { } user && user != open.Ticket.UserId)
            return new(
                PluginRefusalCodes.MediaTicketUserMismatch,
                open.Ticket.PluginId.ToString(),
                "The media link did not play.",
                "The link was minted for a different account on this server.",
                "Open the item from your own account.",
                PluginRefusalSeverity.Blocked
            );

        return null;
    }

    /// <summary>
    /// The ticket, when it may play for <paramref name="deviceId" /> on
    /// <paramref name="pluginId" />'s LAN media route; null otherwise. A user's
    /// ticket, another device's, another plugin's, one that ran out and one
    /// this server never minted all read alike.
    /// </summary>
    public PluginMediaTicket? ReadForDevice(string ticket, Ulid pluginId)
    {
        (PluginMediaTicket Ticket, string Digest, Held Held)? opened = Open(ticket);

        if (opened is not { } open || !open.Held.IsLive(clock.GetUtcNow()))
            return null;

        return open.Ticket is { DeviceId: not null } read && read.PluginId == pluginId
            ? read
            : null;
    }

    /// <summary>
    /// Every ticket minted for a device, gone. Called when the owner revokes
    /// it, so a link it was handed stops at once rather than when it runs out.
    /// </summary>
    public void ForgetDevice(Ulid pluginId, string deviceId)
    {
        lock (_gate)
        {
            if (!_held.Remove((pluginId, $"device:{deviceId}"), out List<string>? minted))
                return;

            foreach (string digest in minted)
                _upstreams.TryRemove(digest, out _);
        }
    }

    /// <summary>How many tickets the server holds right now, live or not yet swept.</summary>
    public int HeldCount => _upstreams.Count;

    /// <summary>
    /// Sweeps when it is due rather than on every mint. Called under
    /// <see cref="_gate" />.
    /// </summary>
    private void SweepIfDue(DateTimeOffset now)
    {
        if (++_mintsSinceSweep < SweepEvery && now - _lastSweep < SweepInterval)
            return;

        Forget(now);
        _mintsSinceSweep = 0;
        _lastSweep = now;
    }

    /// <summary>
    /// Addresses for tickets that can no longer play. Kept out of memory rather
    /// than kept forever: every playback mints one, and a server that runs for
    /// a month would hold every address a plugin ever handed it. Called under
    /// <see cref="_gate" />.
    /// </summary>
    private void Forget(DateTimeOffset now)
    {
        foreach (KeyValuePair<string, Held> held in _upstreams)
        {
            if (!held.Value.IsLive(now))
                _upstreams.TryRemove(held.Key, out _);
        }

        List<(Ulid, string)> emptied = [];

        foreach (KeyValuePair<(Ulid PluginId, string Holder), List<string>> holder in _held)
        {
            holder.Value.RemoveAll(digest => !_upstreams.ContainsKey(digest));

            if (holder.Value.Count == 0)
                emptied.Add(holder.Key);
        }

        foreach ((Ulid, string) key in emptied)
            _held.Remove(key);
    }

    /// <summary>
    /// Brings a holder at the cap back under it: first the tickets of theirs
    /// that can no longer play, then the oldest that were never played, a
    /// batch at a time. A ticket being played is never dropped; a holder whose
    /// every ticket is being played is refused. Called under
    /// <see cref="_gate" />.
    /// </summary>
    private void MakeRoom(List<string> minted, DateTimeOffset now, Ulid pluginId)
    {
        minted.RemoveAll(digest =>
        {
            if (_upstreams.TryGetValue(digest, out Held? held) && held.IsLive(now))
                return false;

            _upstreams.TryRemove(digest, out _);
            return true;
        });

        if (minted.Count < MaxLiveTicketsPerHolder)
            return;

        HashSet<string> evicted = [];

        foreach (string digest in minted)
        {
            if (evicted.Count >= EvictionBatch)
                break;

            if (_upstreams.TryGetValue(digest, out Held? held) && held.LastPlayed is null)
                evicted.Add(digest);
        }

        foreach (string digest in evicted)
            _upstreams.TryRemove(digest, out _);

        minted.RemoveAll(evicted.Contains);

        if (minted.Count < MaxLiveTicketsPerHolder)
            return;

        throw new PluginRefusedException(
            new(
                PluginRefusalCodes.ResourceCeiling,
                pluginId.ToString(),
                $"The server did not mint a media link: {MaxLiveTicketsPerHolder} links minted for one viewer or device are all still playing.",
                "Every link the server mints is held in memory until it runs out, so one viewer or device may hold only so many at once.",
                "Mint links for what is being played now rather than ahead of time, or let some stop before minting more.",
                PluginRefusalSeverity.Degraded
            )
        );
    }

    private byte[] Signature(string body) =>
        HMACSHA256.HashData(serverKey, Encoding.UTF8.GetBytes(body));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');

        return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
    }

    /// <summary>What the server keeps for one ticket.</summary>
    /// <param name="File">For a file ticket, the path relative to the plugin's folder.</param>
    /// <param name="StartBy">The deadline the ticket was minted with: play it by then.</param>
    /// <param name="HardStop">The end, however much it is played.</param>
    /// <param name="LastPlayed">The last request it served, or null before the first.</param>
    /// <param name="DeviceId">For a device's ticket, the device on the owner's network; null for a user's.</param>
    private sealed record Held(
        PluginProxyRequest Request,
        string? File,
        DateTimeOffset StartBy,
        DateTimeOffset HardStop,
        DateTimeOffset? LastPlayed,
        Ulid PluginId,
        string? DeviceId
    )
    {
        public bool IsLive(DateTimeOffset now) =>
            now < HardStop
            && (now < StartBy || (LastPlayed is { } played && now - played < IdleTimeout));
    }
}
