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

using Newtonsoft.Json;

namespace NoMercy.Setup.Cast;

/// <summary>
/// What the receiver should do on boot. Discriminated by <see cref="Type"/>.
/// Mirrors the TypeScript union in §8.2 of the cast-receiver-leanback spec.
/// </summary>
public class CastIntent
{
    [JsonProperty("type")]
    public string Type { get; set; } = "idle";

    /// <summary>Movie / tv — only set when <see cref="Type"/> = "play_video".</summary>
    [JsonProperty("media_type", NullValueHandling = NullValueHandling.Ignore)]
    public string? MediaType { get; set; }

    /// <summary>Movie / tv ID — only set when <see cref="Type"/> = "play_video".</summary>
    [JsonProperty("media_id", NullValueHandling = NullValueHandling.Ignore)]
    public string? MediaId { get; set; }

    /// <summary>Music list type — only set when <see cref="Type"/> = "play_music".</summary>
    [JsonProperty("list_type", NullValueHandling = NullValueHandling.Ignore)]
    public string? ListType { get; set; }

    /// <summary>Music list ID — only set when <see cref="Type"/> = "play_music".</summary>
    [JsonProperty("list_id", NullValueHandling = NullValueHandling.Ignore)]
    public string? ListId { get; set; }

    /// <summary>Optional starting track within a list — only set when <see cref="Type"/> = "play_music".</summary>
    [JsonProperty("track_id", NullValueHandling = NullValueHandling.Ignore)]
    public string? TrackId { get; set; }

    /// <summary>Resume position in seconds (video) or seconds (music). Optional.</summary>
    [JsonProperty("resume_at", NullValueHandling = NullValueHandling.Ignore)]
    public int? ResumeAt { get; set; }

    /// <summary>
    /// The audio language the handing-off device was playing, as an ISO code.
    /// A receiver opens its own transcode session and the server decides which
    /// rendition that session marks default, so without this the television
    /// starts in whatever the file declares — the Japanese dub of an episode the
    /// phone was playing in English.
    /// </summary>
    [JsonProperty("audio_language", NullValueHandling = NullValueHandling.Ignore)]
    public string? AudioLanguage { get; set; }

    /// <summary>
    /// The subtitle the handing-off device showed, as an ISO code. The television
    /// keeps its own caption pick, so without this a cast from a phone showing
    /// English started with no subtitle.
    /// </summary>
    [JsonProperty("subtitle_language", NullValueHandling = NullValueHandling.Ignore)]
    public string? SubtitleLanguage { get; set; }

    /// <summary>The subtitle variant (full, sdh, forced) — English SDH is a different track than English full.</summary>
    [JsonProperty("subtitle_type", NullValueHandling = NullValueHandling.Ignore)]
    public string? SubtitleType { get; set; }

    /// <summary>The subtitle format (ass, vtt) — the .ass carries the show's own styling.</summary>
    [JsonProperty("subtitle_codec", NullValueHandling = NullValueHandling.Ignore)]
    public string? SubtitleCodec { get; set; }

    /// <summary>Vue Router target — only set when <see cref="Type"/> = "navigate".</summary>
    [JsonProperty("route", NullValueHandling = NullValueHandling.Ignore)]
    public string? Route { get; set; }

    public static CastIntent Idle() => new() { Type = "idle" };

    public static CastIntent PlayVideo(
        string mediaType,
        string mediaId,
        int? resumeAt = null,
        string? audioLanguage = null,
        string? subtitleLanguage = null,
        string? subtitleType = null,
        string? subtitleCodec = null
    ) =>
        new()
        {
            Type = "play_video",
            MediaType = mediaType,
            MediaId = mediaId,
            ResumeAt = resumeAt,
            AudioLanguage = audioLanguage,
            SubtitleLanguage = subtitleLanguage,
            SubtitleType = subtitleLanguage is null ? null : subtitleType,
            SubtitleCodec = subtitleLanguage is null ? null : subtitleCodec,
        };

    public static CastIntent PlayMusic(
        string listType,
        string listId,
        string? trackId = null,
        int? resumeAt = null
    ) =>
        new()
        {
            Type = "play_music",
            ListType = listType,
            ListId = listId,
            TrackId = trackId,
            ResumeAt = resumeAt,
        };

    public static CastIntent Navigate(string route) => new() { Type = "navigate", Route = route };
}
