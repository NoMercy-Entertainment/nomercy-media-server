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

using System.Net;
using System.Text;
using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Network;
using NoMercy.PluginSdk.Quotas;

namespace NoMercy.PluginSdk.Media;

/// <summary>
/// What a plugin gets when it asks for a media URL: one on this server.
/// <para>
/// The plugin never builds the address a client sees and never hands a client
/// the upstream. It gives the server somewhere to fetch from, and gets back a
/// link that is checked, short-lived and bound to the one account that asked.
/// </para>
/// </summary>
public class PluginMediaProxy(
    Ulid pluginId,
    IPluginCallerAccessor caller,
    IPluginCapabilityBroker broker,
    PluginMediaTicketMinter minter,
    HttpClient http,
    ILogger logger,
    PluginQuotaMeter? quotas = null
) : IPluginMediaProxy, IPluginMediaFetcher
{
    // Enough for a byte order mark, a little whitespace and "#EXTM3U".
    private const int SniffLength = 64;

    private const string RewrittenPlaylistType = "application/vnd.apple.mpegurl";

    private static readonly string[] PlaylistTypes =
    [
        "application/vnd.apple.mpegurl",
        "application/x-mpegurl",
        "audio/mpegurl",
        "audio/x-mpegurl",
    ];

    // IPTV panels and CDNs serve playlists under these as often as under the
    // right type. An answer under one of them, or under none, is looked at
    // rather than trusted.
    private static readonly string[] SniffedTypes =
    [
        "application/octet-stream",
        "binary/octet-stream",
        "text/plain",
        "video/mp2t",
    ];

    private static readonly byte[] PlaylistMarker = "#EXTM3U"u8.ToArray();

    public static TimeSpan TicketLifetime { get; } = TimeSpan.FromMinutes(15);

    public Task<PluginMediaUrl> MintAsync(
        PluginProxyRequest request,
        CancellationToken ct = default
    )
    {
        if (request.Links.Count == 0)
            throw new PluginRefusedException(
                new(
                    PluginRefusalCodes.CapabilityScopeRefused,
                    pluginId.ToString(),
                    "The server did not mint a media link.",
                    "The request named no address to fetch from.",
                    "Give context.Media.Proxy at least one link.",
                    PluginRefusalSeverity.Blocked
                )
            );

        // Every host, not just the first. A request that falls through to a
        // second link must not reach a host the manifest never named, which is
        // what checking only the one being played would allow.
        foreach (PluginProxyLink link in request.Links)
        {
            if (
                broker.Check(pluginId, PluginCapabilityNames.MediaProxy, link.Url.Host) is
                { } refusal
            )
                throw new PluginRefusedException(refusal);
        }

        return Task.FromResult(Minted(request));
    }

    public Task<PluginMediaUrl> MintImageAsync(Uri source, CancellationToken ct = default) =>
        MintAsync(new() { Links = [new() { Url = source }], PassRange = false }, ct);

    /// <summary>
    /// Fetches what a ticket points at, trying each link in turn. A provider
    /// that drops a connection falls through to the next rather than ending
    /// the viewer's playback.
    /// </summary>
    public async Task<HttpResponseMessage> FetchAsync(
        PluginProxyRequest request,
        string? range,
        CancellationToken ct
    )
    {
        HttpResponseMessage? last = null;

        foreach (PluginProxyLink link in request.Links)
        {
            last = await FetchOneAsync(request, link, range, ct);

            // 206 is a 2xx, so a range answer counts as working here without
            // naming it: a seek is the request being served, not one failing.
            if (last.IsSuccessStatusCode)
                return await MeteredAsync(last, ct);

            logger.LogInformation(
                "Plugin {PluginId}: a media link answered {StatusCode}, trying the next one.",
                pluginId,
                last.StatusCode
            );

            // The answer nobody is going to read still holds a connection, and
            // a provider with four dead links would hold four of them for as
            // long as the viewer keeps trying.
            if (link != request.Links[^1])
                last.Dispose();
        }

        return last ?? new(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// Every byte the client is about to receive, counted against the plugin's
    /// uplink allowance. Counted here rather than from the length header: a
    /// live stream declares no length, and that is exactly the plugin that can
    /// spend an uplink all evening.
    /// </summary>
    private async Task<HttpResponseMessage> MeteredAsync(
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        if (quotas is null)
            return response;

        StreamContent metered = new(
            quotas.Metered(pluginId, await response.Content.ReadAsStreamAsync(ct))
        );

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            metered.Headers.TryAddWithoutValidation(header.Key, header.Value);

        response.Content = metered;

        return response;
    }

    private async Task<HttpResponseMessage> FetchOneAsync(
        PluginProxyRequest request,
        PluginProxyLink link,
        string? range,
        CancellationToken ct
    )
    {
        // Resolved here rather than when the ticket was minted: a credential
        // in an address has a shorter life than the ticket does, and this runs
        // on the server where the answer never reaches a client.
        Uri address = link.ResolveAsync is null ? link.Url : await link.ResolveAsync(ct);

        HttpRequestMessage message = new(HttpMethod.Get, address);

        if (request.PassRange && range is { Length: > 0 })
            message.Headers.TryAddWithoutValidation("Range", range);

        if (link.Referer is not null)
            message.Headers.TryAddWithoutValidation("Referer", link.Referer);

        // The user agent this viewer's player would have sent. The handler
        // keeps it only because this option says the request is a media fetch
        // for a viewer; a plugin's own requests cannot set it.
        if (link.UserAgent is { Length: > 0 } agent && !agent.Any(char.IsControl))
        {
            message.Headers.TryAddWithoutValidation("User-Agent", agent);
            message.Options.Set(PluginUserAgentHandler.ViewerUserAgent, true);
        }

        // Owned by whoever asked: the body is a stream the viewer reads, so it
        // outlives this method and the caller disposes it.
        HttpResponseMessage response = await http.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );

        if (!request.RewriteHlsPlaylists)
            return response;

        string? type = response.Content.Headers.ContentType?.MediaType;

        if (IsOneOf(type, PlaylistTypes))
            return RewritePlaylist(
                request,
                link,
                address,
                response,
                await response.Content.ReadAsStringAsync(ct),
                type!
            );

        if (!response.IsSuccessStatusCode || !(type is null || IsOneOf(type, SniffedTypes)))
            return response;

        return await SniffAsync(request, link, address, response, ct);
    }

    /// <summary>
    /// Looks at the first bytes of an answer whose type says nothing useful.
    /// A playlist is rewritten like any other. Anything else is handed on
    /// whole, the bytes already read put back in front of the rest.
    /// </summary>
    private async Task<HttpResponseMessage> SniffAsync(
        PluginProxyRequest request,
        PluginProxyLink link,
        Uri address,
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        Stream body = await response.Content.ReadAsStreamAsync(ct);
        byte[] head = new byte[SniffLength];
        int read = await ReadAtMostAsync(body, head, ct);

        if (StartsLikeAPlaylist(head.AsSpan(0, read)))
        {
            MemoryStream whole = new();
            await whole.WriteAsync(head.AsMemory(0, read), ct);
            await body.CopyToAsync(whole, ct);
            await body.DisposeAsync();
            whole.Position = 0;

            using StreamReader reader = new(whole, Encoding.UTF8);
            string text = await reader.ReadToEndAsync(ct);

            return RewritePlaylist(request, link, address, response, text, RewrittenPlaylistType);
        }

        StreamContent replayed = new(new PrefixedStream(head, read, body));

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            replayed.Headers.TryAddWithoutValidation(header.Key, header.Value);

        response.Content = replayed;

        return response;
    }

    /// <summary>Fills the buffer, or reads to the end of a shorter answer.</summary>
    private static async Task<int> ReadAtMostAsync(Stream body, byte[] buffer, CancellationToken ct)
    {
        int total = 0;

        while (total < buffer.Length)
        {
            int read = await body.ReadAsync(buffer.AsMemory(total), ct);

            if (read == 0)
                break;

            total += read;
        }

        return total;
    }

    /// <summary>"#EXTM3U", after an optional byte order mark and whitespace.</summary>
    private static bool StartsLikeAPlaylist(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            head = head[3..];

        int start = 0;
        // Space, tab, carriage return, line feed.
        while (start < head.Length && head[start] is 0x20 or 0x09 or 0x0D or 0x0A)
            start++;

        return head[start..].StartsWith(PlaylistMarker);
    }

    private static bool IsOneOf(string? type, string[] types) =>
        type is not null && types.Contains(type, StringComparer.OrdinalIgnoreCase);

    private PluginMediaUrl Minted(PluginProxyRequest request)
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(TicketLifetime);
        string ticket = minter.Mint(pluginId, Asking(), request, TicketLifetime);

        return PluginMediaUrl.Minted(
            new($"/api/v1/plugins/{pluginId}/media/{ticket}", UriKind.Relative),
            expiresAt,
            default
        );
    }

    /// <summary>
    /// The account this link is for. Refused rather than minted for nobody: a
    /// ticket bound to the empty account is a ticket anybody can play, and a
    /// plugin minting one outside a request has nobody to play it.
    /// </summary>
    private Guid Asking()
    {
        Guid userId = caller.CurrentUserId;

        if (userId != Guid.Empty)
            return userId;

        throw new PluginRefusedException(
            new(
                PluginRefusalCodes.MediaTicketUserMismatch,
                pluginId.ToString(),
                "The server did not mint a media link.",
                "Nothing is asking for it, so there is no account to bind it to.",
                "Mint the link while serving a request. A scheduled job has no viewer to mint for.",
                PluginRefusalSeverity.Blocked
            )
        );
    }

    /// <summary>
    /// Every child address in a playlist becomes another ticket on this
    /// server. One that did not would send the client straight to the provider
    /// for the segments, which is the client holding the upstream after all.
    /// <para>
    /// The children carry the headers of the link that answered, not of the
    /// first link. A channel whose first link is dead plays from its second,
    /// and the segments have to be asked for the way the playlist was.
    /// </para>
    /// </summary>
    private HttpResponseMessage RewritePlaylist(
        PluginProxyRequest request,
        PluginProxyLink answered,
        Uri address,
        HttpResponseMessage response,
        string body,
        string mediaType
    )
    {
        Uri baseUri = response.RequestMessage?.RequestUri ?? address;

        IEnumerable<string> rewritten = body.Split('\n')
            .Select(line =>
            {
                string trimmed = line.Trim();

                if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                    return line;

                PluginProxyRequest child = request with
                {
                    Links =
                    [
                        new()
                        {
                            Url = new(baseUri, trimmed),
                            Referer = answered.Referer,
                            UserAgent = answered.UserAgent,
                        },
                    ],
                };

                return $"/api/v1/plugins/{pluginId}/media/{minter.Mint(pluginId, Asking(), child, TicketLifetime)}";
            });

        string text = string.Join('\n', rewritten);
        HttpStatusCode status = response.StatusCode;

        // The upstream's answer is read and done with; what goes out is new.
        response.Dispose();

        return new(status) { Content = new StringContent(text, Encoding.UTF8, mediaType) };
    }
}
