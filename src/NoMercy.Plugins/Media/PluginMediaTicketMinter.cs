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
    private readonly ConcurrentDictionary<string, Held> _upstreams = new();

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

    public string Mint(Ulid pluginId, Guid userId, PluginProxyRequest request, TimeSpan lifetime)
    {
        DateTimeOffset now = clock.GetUtcNow();
        DateTimeOffset expiresAt = now.Add(lifetime);
        DateTimeOffset hardStop = now.Add(MaxLifetime);

        // A fresh handle per ticket rather than a hash of the address. Two
        // viewers opening the same stream get their own ticket, and one
        // running out does not take the other's address with it.
        string digest = Ulid.NewUlid().ToString();

        Forget();
        _upstreams[digest] = new(
            request,
            expiresAt,
            hardStop > expiresAt ? hardStop : expiresAt,
            null
        );

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
                ),
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
    /// Addresses for tickets that can no longer play. Kept out of memory rather
    /// than kept forever: every playback mints one, and a server that runs for
    /// a month would hold every address a plugin ever handed it.
    /// </summary>
    private void Forget()
    {
        DateTimeOffset now = clock.GetUtcNow();

        foreach (KeyValuePair<string, Held> held in _upstreams)
        {
            if (!held.Value.IsLive(now))
                _upstreams.TryRemove(held.Key, out _);
        }
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
    /// <param name="StartBy">The deadline the ticket was minted with: play it by then.</param>
    /// <param name="HardStop">The end, however much it is played.</param>
    /// <param name="LastPlayed">The last request it served, or null before the first.</param>
    private sealed record Held(
        PluginProxyRequest Request,
        DateTimeOffset StartBy,
        DateTimeOffset HardStop,
        DateTimeOffset? LastPlayed
    )
    {
        public bool IsLive(DateTimeOffset now) =>
            now < HardStop
            && (now < StartBy || (LastPlayed is { } played && now - played < IdleTimeout));
    }
}
