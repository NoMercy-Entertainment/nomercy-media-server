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
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Media;

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
    Guid callerId,
    IPluginCapabilityBroker broker,
    PluginMediaTicketMinter minter,
    HttpClient http,
    ILogger logger
) : IPluginMediaProxy
{
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
                return last;

            logger.LogInformation(
                "Plugin {PluginId}: a media link answered {StatusCode}, trying the next one.",
                pluginId,
                last.StatusCode
            );
        }

        return last ?? new(HttpStatusCode.NotFound);
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

        if (link.UserAgent is not null)
            message.Headers.TryAddWithoutValidation("User-Agent", link.UserAgent);

        HttpResponseMessage response = await http.SendAsync(
            message,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );

        if (!request.RewriteHlsPlaylists || !IsPlaylist(response))
            return response;

        return await RewritePlaylistAsync(request, address, response, ct);
    }

    private PluginMediaUrl Minted(PluginProxyRequest request)
    {
        DateTimeOffset expiresAt = DateTimeOffset.UtcNow.Add(TicketLifetime);
        string ticket = minter.Mint(pluginId, callerId, request, TicketLifetime);

        return PluginMediaUrl.Minted(
            new($"/api/v1/plugins/{pluginId}/media/{ticket}", UriKind.Relative),
            expiresAt,
            default
        );
    }

    private static bool IsPlaylist(HttpResponseMessage response) =>
        response.Content.Headers.ContentType?.MediaType
            is "application/vnd.apple.mpegurl"
                or "application/x-mpegurl"
                or "audio/mpegurl";

    /// <summary>
    /// Every child address in a playlist becomes another ticket on this
    /// server. One that did not would send the client straight to the provider
    /// for the segments, which is the client holding the upstream after all.
    /// </summary>
    private async Task<HttpResponseMessage> RewritePlaylistAsync(
        PluginProxyRequest request,
        Uri address,
        HttpResponseMessage response,
        CancellationToken ct
    )
    {
        string body = await response.Content.ReadAsStringAsync(ct);
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
                            Referer = request.Links[0].Referer,
                            UserAgent = request.Links[0].UserAgent,
                        },
                    ],
                };

                return $"/api/v1/plugins/{pluginId}/media/{minter.Mint(pluginId, callerId, child, TicketLifetime)}";
            });

        return new(response.StatusCode)
        {
            Content = new StringContent(
                string.Join('\n', rewritten),
                Encoding.UTF8,
                response.Content.Headers.ContentType?.MediaType ?? "application/vnd.apple.mpegurl"
            ),
        };
    }
}
