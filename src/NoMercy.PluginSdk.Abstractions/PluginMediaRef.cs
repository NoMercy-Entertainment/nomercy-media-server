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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// What a media card carries, so every client plays it through the real player.
///
/// <para>
/// The url is minted by the host, short lived and bound to the caller. A plugin
/// never builds one and a client never appends to one: a token in a url is a
/// credential in a browser history, a proxy log and a screenshot, which is what
/// <see cref="PluginRefusalCodes" /> refuses.
/// </para>
///
/// <para>
/// Kind decides which player, not which screen. Audio reaches the music engine
/// wherever the viewer is; video reaches the video player. Before this existed
/// a plugin's stream was drawn by a card that played it in a second, smaller
/// player of its own, with no artist, no cover and none of the remote handling
/// the real one has.
/// </para>
/// </summary>
public class PluginMediaRef
{
    [JsonPropertyName("pluginId")]
    public required string PluginId { get; init; }

    /// <summary>The plugin's own id for this media, which it gets back unchanged.</summary>
    [JsonPropertyName("mediaId")]
    public required string MediaId { get; init; }

    /// <summary>One of <see cref="PluginMediaKind" />.</summary>
    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    /// <summary>Whether this has no end, which decides what the transport shows.</summary>
    [JsonPropertyName("live")]
    public bool Live { get; init; }

    /// <summary>Host minted, short lived, user bound. Never built by a plugin.</summary>
    [JsonPropertyName("url")]
    public required string Url { get; init; }

    [JsonPropertyName("title")]
    public required string Title { get; init; }

    [JsonPropertyName("artist")]
    public string? Artist { get; init; }

    [JsonPropertyName("cover")]
    public string? Cover { get; init; }
}

/// <summary>
/// Which player a media reference reaches.
///
/// Two words rather than a per-plugin choice: a client has one music engine
/// and one video player, and a third answer would mean a plugin could ask for
/// a player no client has.
/// </summary>
public static class PluginPlayerKind
{
    public const string Audio = "audio";
    public const string Video = "video";

    public static IReadOnlyList<string> All { get; } = [Audio, Video];

    public static bool IsKnown(string? kind)
    {
        return kind is not null && All.Contains(kind);
    }
}
