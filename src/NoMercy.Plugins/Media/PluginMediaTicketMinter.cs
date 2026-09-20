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
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Media;

/// <summary>
/// The host mints every media link a plugin hands out.
/// <para>
/// A plugin gives the server an upstream and gets back a ticket naming the
/// plugin, the one account it is for, and a deadline. The upstream itself
/// never crosses the wire: the ticket carries a hash of it and the server
/// keeps the address. A link a viewer can read is a link a viewer can share,
/// and an upstream that carries a credential would then be shared with it.
/// </para>
/// </summary>
public class PluginMediaTicketMinter(TimeProvider clock, byte[] serverKey)
{
    private readonly ConcurrentDictionary<
        string,
        (string Upstream, DateTimeOffset Until)
    > _upstreams = new();

    public string Mint(Ulid pluginId, Guid userId, string upstream, TimeSpan lifetime)
    {
        DateTimeOffset expiresAt = clock.GetUtcNow().Add(lifetime);
        string digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(upstream)));

        Forget();
        _upstreams[digest] = (upstream, expiresAt);

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

    public PluginMediaTicket? Read(string ticket)
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

            if (
                !_upstreams.TryGetValue(
                    root.GetProperty("s").GetString() ?? string.Empty,
                    out (string Upstream, DateTimeOffset Until) held
                )
            )
                return null;

            return new(
                Ulid.Parse(root.GetProperty("p").GetString()!),
                Guid.Parse(root.GetProperty("u").GetString()!),
                held.Upstream,
                DateTimeOffset.FromUnixTimeSeconds(root.GetProperty("e").GetInt64())
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
        PluginMediaTicket? read = Read(ticket);

        if (read is null || read.ExpiresAt <= clock.GetUtcNow())
            return new(
                PluginRefusalCodes.MediaTicketExpired,
                read?.PluginId.ToString() ?? "unknown",
                "The media link did not play.",
                "The link has run out. This server mints a new one for every playback, so they are short-lived by design.",
                "Open the item again from the plugin's page.",
                PluginRefusalSeverity.Blocked
            );

        if (asking is { } user && user != read.UserId)
            return new(
                PluginRefusalCodes.MediaTicketUserMismatch,
                read.PluginId.ToString(),
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

        foreach (KeyValuePair<string, (string Upstream, DateTimeOffset Until)> held in _upstreams)
        {
            if (held.Value.Until <= now)
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
}
