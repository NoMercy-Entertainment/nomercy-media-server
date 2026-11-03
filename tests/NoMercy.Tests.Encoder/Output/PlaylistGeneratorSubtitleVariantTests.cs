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

using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Tests.Encoder.Output;

/// <summary>
/// Deep coverage of the subtitle-variant + URI + display-name logic in
/// <see cref="PlaylistGenerator"/>. The existing tests cover the master
/// playlist's TYPE=SUBTITLES emission and the WebVTT/ASS media playlist
/// shape, but the variant labelling ("Signs &amp; Songs" / "SDH" / "Alt")
/// and the codec-driven URI scheme (.ass / .srt / .m3u8) determine what
/// shows up in the player's subtitle picker and what file extension the
/// client requests. A wrong label or URI silently breaks the picker.
///
/// Each category below pins a specific failure mode:
///
/// • Variant display name — "sign" → "(Signs &amp; Songs)", "sdh" → "(SDH)",
///   "alt" → "(Alt)", anything else → bare language name. Picker users
///   rely on this to distinguish multiple tracks of the same language.
/// • FORCED tag — YES only for variant=="sign" (case-insensitive). Picker
///   uses this to auto-select signs/songs alongside the main audio.
/// • URI scheme — Ass codec → .ass file URI, Srt → .srt, anything else
///   (WebVtt, etc.) → .m3u8 wrapper. Wrong codec lands a 404 in the player.
/// • Variant null/empty default — both fall back to "full" so the URI is
///   always well-formed even on minimal plans.
/// • Language null fallback — "und" (ISO 639-2) used uniformly.
/// • Extract action surfaces in the master playlist (existing tests only
///   verify Drop is excluded).
/// • ASS sidecar playlist shape — single EXTINF entry, no segmentation.
/// • WebVTT segment playlist shape — one EXTINF per segment, ends with
///   EXT-X-ENDLIST.
/// </summary>
public class PlaylistGeneratorSubtitleVariantTests
{
    private const string MediaTitle = "Movie.Name";

    // ── Variant display names ────────────────────────────────────────────────

    [Theory]
    [InlineData("sign", "Signs & Songs")]
    [InlineData("sdh", "SDH")]
    [InlineData("alt", "Alt")]
    public void Master_playlist_subtitle_name_carries_variant_suffix(
        string variant,
        string expectedSuffix
    )
    {
        // Picker shows "English (SDH)" — without the suffix users can't
        // distinguish multiple tracks of the same language.
        string playlist = Generate(
            Plan([
                SubtitleEntry(language: "eng", variant: variant, codec: SubtitleCodecType.WebVtt),
            ])
        );

        playlist.Should().Contain($"({expectedSuffix})");
    }

    [Theory]
    [InlineData("SIGN")]
    [InlineData("Sign")]
    [InlineData("sIgN")]
    public void Master_playlist_subtitle_name_variant_match_is_case_insensitive(string variant)
    {
        // The variant switch uses ToLowerInvariant — any casing of "sign"
        // must map to the Signs & Songs label.
        string playlist = Generate(
            Plan([
                SubtitleEntry(language: "eng", variant: variant, codec: SubtitleCodecType.WebVtt),
            ])
        );

        playlist.Should().Contain("(Signs & Songs)");
    }

    [Fact]
    public void Master_playlist_subtitle_name_unknown_variant_uses_bare_language()
    {
        // Unknown variants (e.g. "commentary") fall through to the default
        // arm and emit just the English language name with no suffix.
        string playlist = Generate(
            Plan([
                SubtitleEntry(
                    language: "eng",
                    variant: "commentary",
                    codec: SubtitleCodecType.WebVtt
                ),
            ])
        );

        playlist.Should().NotContain("(Commentary)");
        playlist.Should().NotContain("(commentary)");
    }

    [Fact]
    public void Master_playlist_subtitle_name_default_variant_uses_bare_language()
    {
        // Default variant "full" should also emit just the language name.
        string playlist = Generate(
            Plan([SubtitleEntry(language: "eng", variant: "full", codec: SubtitleCodecType.WebVtt)])
        );

        // The bare English name appears as the NAME attribute, not "English (Full)".
        playlist.Should().NotContain("(Full)");
        playlist.Should().NotContain("(full)");
    }

    // ── FORCED attribute ─────────────────────────────────────────────────────

    [Fact]
    public void Master_playlist_subtitle_forced_yes_only_for_sign_variant()
    {
        // Signs & Songs auto-plays alongside foreign-language scenes.
        string playlist = Generate(
            Plan([
                SubtitleEntry(language: "eng", variant: "full", codec: SubtitleCodecType.WebVtt),
                SubtitleEntry(language: "eng", variant: "sign", codec: SubtitleCodecType.WebVtt),
            ])
        );

        // Two EXT-X-MEDIA lines — one FORCED=NO, one FORCED=YES.
        int forcedNoCount = CountOccurrences(playlist, "FORCED=NO");
        int forcedYesCount = CountOccurrences(playlist, "FORCED=YES");

        forcedNoCount.Should().Be(1);
        forcedYesCount.Should().Be(1);
    }

    [Fact]
    public void Master_playlist_subtitle_forced_no_for_sdh_variant()
    {
        // SDH is hearing-accessibility, not foreign-scene — it's a user
        // preference, not auto-selected.
        string playlist = Generate(
            Plan([SubtitleEntry(language: "eng", variant: "sdh", codec: SubtitleCodecType.WebVtt)])
        );

        playlist.Should().Contain("FORCED=NO");
        playlist.Should().NotContain("FORCED=YES");
    }

    // ── URI scheme by codec ──────────────────────────────────────────────────

    [Fact]
    public void GetSubtitlePlaylistUri_for_ass_codec_returns_ass_extension()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "jpn",
            variant: "full",
            codec: SubtitleCodecType.Ass
        );

        string uri = PlaylistGenerator.GetSubtitlePlaylistUri(sub);

        uri.Should().Be("subtitles/jpn/full.ass");
    }

    [Fact]
    public void GetSubtitlePlaylistUri_for_srt_codec_returns_srt_extension()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "fre",
            variant: "sdh",
            codec: SubtitleCodecType.Srt
        );

        string uri = PlaylistGenerator.GetSubtitlePlaylistUri(sub);

        uri.Should().Be("subtitles/fre/sdh.srt");
    }

    [Fact]
    public void GetSubtitlePlaylistUri_for_webvtt_codec_returns_m3u8_wrapper()
    {
        // WebVTT goes through a segmented playlist because HLS spec requires
        // EXT-X-MEDIA to reference a media playlist (m3u8), not a raw file.
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "eng",
            variant: "full",
            codec: SubtitleCodecType.WebVtt
        );

        string uri = PlaylistGenerator.GetSubtitlePlaylistUri(sub);

        uri.Should().Be("subtitles/eng/full.m3u8");
    }

    [Fact]
    public void GetSubtitlePlaylistUri_with_null_language_uses_und_token()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: null,
            variant: "full",
            codec: SubtitleCodecType.Srt
        );

        string uri = PlaylistGenerator.GetSubtitlePlaylistUri(sub);

        uri.Should().Be("subtitles/und/full.srt");
    }

    [Fact]
    public void GetSubtitlePlaylistUri_with_empty_variant_defaults_to_full()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "eng",
            variant: "",
            codec: SubtitleCodecType.WebVtt
        );

        string uri = PlaylistGenerator.GetSubtitlePlaylistUri(sub);

        uri.Should().Be("subtitles/eng/full.m3u8");
    }

    // ── Action filter (Extract appears in master, Drop excluded) ─────────────

    [Fact]
    public void Master_playlist_includes_extract_action_subtitles()
    {
        // Extract is the most common path (PGS bitmap subtitles → WebVTT).
        // Must surface in the master playlist alongside Copy entries.
        string playlist = Generate(
            Plan([
                SubtitleEntry(
                    language: "eng",
                    variant: "full",
                    codec: SubtitleCodecType.WebVtt,
                    action: StreamAction.Extract
                ),
            ])
        );

        playlist.Should().Contain("TYPE=SUBTITLES");
        playlist.Should().Contain("LANGUAGE=\"eng\"");
    }

    [Fact]
    public void Master_playlist_excludes_transcode_action_subtitles()
    {
        // Transcode is the planner saying "burn-in" or similar — not an
        // independent track for the picker.
        string playlist = Generate(
            Plan([
                SubtitleEntry(
                    language: "eng",
                    variant: "full",
                    codec: SubtitleCodecType.WebVtt,
                    action: StreamAction.Transcode
                ),
            ])
        );

        playlist.Should().NotContain("TYPE=SUBTITLES");
    }

    [Fact]
    public void Stream_inf_carries_subtitles_attribute_and_closed_captions_none_when_active_subs_present()
    {
        // The video variant line must signal SUBTITLES="subs" so clients
        // know to pick up the subtitle group. A subtitle group says nothing
        // about captions, so CLOSED-CAPTIONS=NONE is still written: without
        // it a player probes the video for CEA-608 and can invent a track.
        string playlist = Generate(
            Plan([SubtitleEntry(language: "eng", variant: "full", codec: SubtitleCodecType.WebVtt)])
        );

        playlist.Should().Contain("SUBTITLES=\"subs\"");
        playlist
            .Should()
            .Contain(
                "CLOSED-CAPTIONS=NONE",
                "every variant states it has no caption group, also when it has a subtitle group"
            );
    }

    [Fact]
    public void Stream_inf_falls_back_to_closed_captions_none_when_no_active_subs()
    {
        // No active subtitles → stream-inf must explicitly state CLOSED-CAPTIONS=NONE
        // so hls.js doesn't try to extract CEA-608/708 from the H.264 stream,
        // and must not name a subtitle group the master never defines.
        string playlist = Generate(Plan(Array.Empty<SubtitleOutputPlan>()));

        playlist.Should().NotContain("SUBTITLES=");
        playlist.Should().Contain("CLOSED-CAPTIONS=NONE");
    }

    [Fact]
    public void Master_playlist_omits_webvtt_subs_when_chunking_disabled()
    {
        // HlsDerivatives.SubtitleWebVtt=false (EmitSubtitleWebVttChunks=false
        // on the plan): the raw .vtt extract still exists on disk for
        // download, but the m3u8 wrapper isn't generated. Master playlist
        // must skip the EXT-X-MEDIA subtitle entry — referencing a missing
        // m3u8 would 404 on every spec-compliant client.
        string playlist = Generate(
            PlanWithoutChunks([
                SubtitleEntry(language: "eng", variant: "full", codec: SubtitleCodecType.WebVtt),
            ])
        );

        playlist.Should().NotContain("TYPE=SUBTITLES");
        playlist
            .Should()
            .NotContain(
                "SUBTITLES=",
                "a variant must never name a subtitle group the master does not define"
            );
        playlist.Should().Contain("CLOSED-CAPTIONS=NONE");
    }

    [Fact]
    public void Master_playlist_keeps_ass_sidecars_when_chunking_disabled()
    {
        // Chunking only governs WebVTT — ASS/SRT sidecars play directly from
        // the file URI and don't need a media playlist. They stay in the
        // master even when WebVTT chunking is off.
        string playlist = Generate(
            PlanWithoutChunks([
                SubtitleEntry(language: "jpn", variant: "full", codec: SubtitleCodecType.Ass),
            ])
        );

        playlist.Should().Contain("TYPE=SUBTITLES");
        playlist.Should().Contain("subtitles/jpn/full.ass");
    }

    // ── ASS sidecar media playlist ───────────────────────────────────────────

    [Fact]
    public void GenerateAssMediaPlaylist_single_entry_no_segmentation()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "jpn",
            variant: "full",
            codec: SubtitleCodecType.Ass
        );

        string playlist = PlaylistGenerator.GenerateAssMediaPlaylist(
            sub,
            "subs.jpn.full.ass",
            segmentDurationSeconds: 6
        );

        playlist.Should().Contain("#EXTM3U");
        playlist.Should().Contain("#EXT-X-VERSION:3");
        playlist.Should().Contain("#EXT-X-TARGETDURATION:6");
        playlist.Should().Contain("#EXT-X-PLAYLIST-TYPE:VOD");
        playlist.Should().Contain("#EXTINF:6.000,");
        playlist.Should().Contain("subs.jpn.full.ass");
        playlist.Should().EndWith("#EXT-X-ENDLIST" + Environment.NewLine);
    }

    // ── WebVTT segment media playlist ────────────────────────────────────────

    [Fact]
    public void GenerateSubtitleMediaPlaylist_emits_one_extinf_per_segment()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "eng",
            variant: "full",
            codec: SubtitleCodecType.WebVtt
        );
        WebVttSegment[] segments =
        [
            new(
                Index: 0,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromSeconds(6)
            ),
            new(
                Index: 1,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.FromSeconds(6),
                EndTime: TimeSpan.FromSeconds(12)
            ),
            new(
                Index: 2,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.FromSeconds(12),
                EndTime: TimeSpan.FromSeconds(14.5)
            ),
        ];

        string playlist = PlaylistGenerator.GenerateSubtitleMediaPlaylist(
            sub,
            segments,
            segmentDurationSeconds: 6
        );

        playlist.Should().Contain("#EXTM3U");
        playlist.Should().Contain("#EXT-X-VERSION:3");
        playlist.Should().Contain("#EXT-X-TARGETDURATION:6");
        playlist.Should().Contain("#EXT-X-PLAYLIST-TYPE:VOD");

        // Each segment line: EXTINF + uri. 3 segments → 3 EXTINFs.
        CountOccurrences(playlist, "#EXTINF:").Should().Be(3);
        playlist.Should().Contain("#EXTINF:6.000,");
        playlist.Should().Contain("#EXTINF:2.500,"); // partial last segment
        // Playlist lives in subtitles/eng/ — segment URIs are relative to it.
        playlist.Should().Contain("full_00000.vtt");
        playlist.Should().Contain("full_00001.vtt");
        playlist.Should().Contain("full_00002.vtt");
        playlist.Should().EndWith("#EXT-X-ENDLIST" + Environment.NewLine);
    }

    [Fact]
    public void GenerateSubtitleMediaPlaylist_segment_uri_prefix_prepends_path()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "eng",
            variant: "sdh",
            codec: SubtitleCodecType.WebVtt
        );
        WebVttSegment[] segments =
        [
            new(
                Index: 0,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromSeconds(6)
            ),
        ];

        string playlist = PlaylistGenerator.GenerateSubtitleMediaPlaylist(
            sub,
            segments,
            segmentDurationSeconds: 6,
            segmentUriPrefix: "subtitles"
        );

        playlist.Should().Contain("subtitles/sdh_00000.vtt");
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("nl-NL")]
    [InlineData("fr-FR")]
    public void GenerateSubtitleMediaPlaylist_extinf_duration_uses_invariant_decimal(string culture)
    {
        // HLS spec requires a period as decimal separator. On comma-decimal
        // locales (de-DE / nl-NL / fr-FR) the duration must NOT come out as
        // "6,000" — that would be parsed as 6000 seconds by spec-compliant
        // players. Pins the fix that landed alongside this test file.
        SubtitleOutputPlan sub = SubtitleEntry(
            language: "eng",
            variant: "full",
            codec: SubtitleCodecType.WebVtt
        );
        WebVttSegment[] segments =
        [
            new(
                Index: 0,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromMilliseconds(6_125)
            ),
        ];

        System.Globalization.CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new(culture);

            string playlist = PlaylistGenerator.GenerateSubtitleMediaPlaylist(
                sub,
                segments,
                segmentDurationSeconds: 6
            );

            playlist.Should().Contain("#EXTINF:6.125,").And.NotContain("#EXTINF:6,125");
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    [Fact]
    public void GenerateSubtitleMediaPlaylist_with_null_language_uses_und_in_segment_name()
    {
        SubtitleOutputPlan sub = SubtitleEntry(
            language: null,
            variant: "full",
            codec: SubtitleCodecType.WebVtt
        );
        WebVttSegment[] segments =
        [
            new(
                Index: 0,
                Content: "WEBVTT\n",
                StartTime: TimeSpan.Zero,
                EndTime: TimeSpan.FromSeconds(6)
            ),
        ];

        string playlist = PlaylistGenerator.GenerateSubtitleMediaPlaylist(
            sub,
            segments,
            segmentDurationSeconds: 6
        );

        playlist.Should().Contain("full_00000.vtt");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SubtitleOutputPlan SubtitleEntry(
        string? language,
        string variant,
        SubtitleCodecType codec,
        StreamAction action = StreamAction.Copy
    ) =>
        new(
            OutputCodec: codec,
            Action: action,
            Language: language,
            SourceIndex: 0,
            MapLabel: "0:s:0",
            Variant: variant
        );

    private static OutputPlan Plan(IReadOnlyList<SubtitleOutputPlan> subs) =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs: [Video()],
            AudioOutputs: [Audio()],
            SubtitleOutputs: [.. subs],
            Thumbnails: null
        );

    private static OutputPlan PlanWithoutChunks(IReadOnlyList<SubtitleOutputPlan> subs) =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs: [Video()],
            AudioOutputs: [Audio()],
            SubtitleOutputs: [.. subs],
            Thumbnails: null,
            EmitSubtitleWebVttChunks: false
        );

    private static string Generate(OutputPlan plan)
    {
        Dictionary<string, VariantMetrics> videoMetrics = plan.VideoOutputs.ToDictionary(
            PlaylistGenerator.VideoVariantKey,
            _ => new VariantMetrics(5_000_000, 4_500_000)
        );
        Dictionary<string, VariantMetrics> audioMetrics = plan.AudioOutputs.ToDictionary(
            PlaylistGenerator.AudioVariantKey,
            _ => new VariantMetrics(192_000, 180_000)
        );

        PlaylistGenerator generator = new();
        return generator.GenerateMasterPlaylist(plan, MediaTitle, videoMetrics, audioMetrics);
    }

    private static VideoOutputPlan Video() =>
        new(
            Width: 1920,
            Height: 1080,
            EncoderName: "libx264",
            Crf: 23,
            BitrateKbps: 4000,
            Preset: "medium",
            Profile: "high",
            Level: "4.1",
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: "[v0]",
            ExtraFlags: []
        );

    private static AudioOutputPlan Audio() =>
        new(
            EncoderName: "aac",
            BitrateKbps: 192,
            Channels: 2,
            SampleRate: 48000,
            Action: StreamAction.Transcode,
            Language: "eng",
            MapLabel: "0:a:0"
        );

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }
}
