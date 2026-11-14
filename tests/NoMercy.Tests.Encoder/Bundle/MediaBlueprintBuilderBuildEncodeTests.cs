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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Profiles;

namespace NoMercy.Tests.Encoder.Bundle;

/// <summary>
/// <see cref="MediaBlueprintBuilder.BuildEncode"/> — the completeness
/// invariant (spec "The invariant"): every source stream ends up as exactly
/// one track, and the OCR <c>.vtt</c> sidecar never leaks into a track's
/// <c>files</c>. See <c>.claude/specs/reconstruction-blueprint/SPEC.md</c>.
/// </summary>
public class MediaBlueprintBuilderBuildEncodeTests
{
    private readonly MediaBlueprintBuilder _builder = new();

    // Stream indices, matched by SourceStreamIndex — never by array position.
    private const int VideoIndex = 0; // copied
    private const int TranscodedAudioIndex = 1; // transcoded
    private const int DroppedAudioIndex = 2; // no matching output — dropped
    private const int SubtitleIndex = 3; // preserved bitmap (.mks), OCR .vtt sibling on disk

    private static MediaInfo MakeSource() =>
        new(
            FilePath: "Download/complete/Show/Show.S01E01.mkv",
            Format: "matroska,webm",
            Duration: TimeSpan.FromSeconds(1440),
            OverallBitRateKbps: 8_000,
            FileSizeBytes: 1_500_000_000L,
            VideoStreams:
            [
                new(
                    Index: VideoIndex,
                    Codec: "hevc",
                    Width: 1920,
                    Height: 1080,
                    FrameRate: 23.976,
                    BitDepth: 8,
                    PixelFormat: "yuv420p",
                    ColorPrimaries: "bt709",
                    ColorTransfer: "bt709",
                    ColorSpace: "bt709",
                    IsDefault: true,
                    BitRateKbps: 6_000
                ),
            ],
            AudioStreams:
            [
                new(
                    Index: TranscodedAudioIndex,
                    Codec: "flac",
                    Channels: 2,
                    SampleRate: 48_000,
                    BitRateKbps: 0,
                    Language: "jpn",
                    IsDefault: true,
                    IsForced: false
                ),
                new(
                    Index: DroppedAudioIndex,
                    Codec: "aac",
                    Channels: 2,
                    SampleRate: 48_000,
                    BitRateKbps: 192,
                    Language: "eng",
                    IsDefault: false,
                    IsForced: false
                ),
            ],
            SubtitleStreams:
            [
                new(
                    Index: SubtitleIndex,
                    Codec: "hdmv_pgs_subtitle",
                    Language: "eng",
                    IsDefault: false,
                    IsForced: false
                ),
            ],
            Chapters: [],
            Attachments: []
        );

    private static OutputPlan MakePlan() =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs:
            [
                new(
                    Width: 1920,
                    Height: 1080,
                    EncoderName: "copy",
                    Crf: 0,
                    BitrateKbps: 0,
                    Preset: null,
                    Profile: null,
                    Level: null,
                    TenBit: false,
                    PixelFormat: "yuv420p",
                    MapLabel: "0:v:0",
                    ExtraFlags: [],
                    SourceStreamIndex: VideoIndex
                ),
            ],
            AudioOutputs:
            [
                new(
                    EncoderName: "opus",
                    BitrateKbps: 128,
                    Channels: 2,
                    SampleRate: 48_000,
                    Action: StreamAction.Transcode,
                    Language: "jpn",
                    MapLabel: "0:a:0",
                    SourceStreamIndex: TranscodedAudioIndex
                ),
                // No output carries DroppedAudioIndex — that stream must
                // still surface as its own "dropped" track, never vanish.
            ],
            SubtitleOutputs:
            [
                new(
                    OutputCodec: SubtitleCodecType.Copy,
                    Action: StreamAction.Extract,
                    Language: "eng",
                    SourceIndex: SubtitleIndex,
                    MapLabel: "0:s:0",
                    Policy: SubtitlePolicy.Extract,
                    Variant: "full"
                ),
            ],
            Thumbnails: null
        );

    private static BundleLayout MakeLayout() =>
        new(
            MediaKey: "abc123",
            PresetSlug: "anime-1080p",
            IsSingleFile: false,
            BundleDirectory: "encodes/anime-1080p",
            MasterPlaylistName: "abc123_master.m3u8",
            ManifestPath: "encodes/anime-1080p/manifest.json",
            ReconstructionPath: "encodes/anime-1080p/reconstruction.json",
            SingleFileName: string.Empty,
            PresetId: "01HZPRESET",
            PresetName: "Anime 1080p",
            ContainerString: "hls-fmp4"
        );

    // Real on-disk listing, relative to the media folder root — mirrors what
    // FinalizeStage's allEntries produces. The bitmap subtitle carries BOTH
    // its preserved .mks AND its OCR .vtt sibling; the track must point at
    // the .mks only.
    private static List<string> MakeOutputFiles() =>
        [
            "abc123_master.m3u8",
            "video_1920x1080_SDR/video_1920x1080_SDR_init.mp4",
            "video_1920x1080_SDR/video_1920x1080_SDR_00001.m4s",
            "audio_jpn_opus/audio_jpn_opus_init.mp4",
            "audio_jpn_opus/audio_jpn_opus_00001.m4s",
            "subtitles/Show.S01E01.NoMercy.eng.full.mks",
            "subtitles/Show.S01E01.NoMercy.eng.full.vtt",
        ];

    private BlueprintEncode BuildEncode() =>
        _builder.BuildEncode(
            MakeSource(),
            MakePlan(),
            MakeLayout(),
            MakeOutputFiles(),
            outputLocation: "Anime/Show/Season 01/Show S01E01",
            encoderVersion: "1.2.3",
            profileFingerprint: "fingerprint-abc",
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc)
        );

    // AUD-1174: a track tag never leaves the output folder, and the track
    // still finds its file under the cleaned name.
    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("..", "und")]
    public void PreservedSubtitle_LanguageWithPathSegments_IsFoundByItsCleanedFileName(
        string language,
        string expected
    )
    {
        MediaInfo source = MakeSource();
        source = source with
        {
            SubtitleStreams = [source.SubtitleStreams[0] with { Language = language }],
        };
        string preserved = $"subtitles/Show.S01E01.NoMercy.{expected}.full.mks";

        BlueprintEncode encode = _builder.BuildEncode(
            source,
            MakePlan(),
            MakeLayout(),
            [preserved],
            outputLocation: "Anime/Show/Season 01/Show S01E01",
            encoderVersion: "1.2.3",
            profileFingerprint: "fingerprint-abc",
            createdAt: new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            completedAt: new DateTime(2026, 1, 1, 0, 10, 0, DateTimeKind.Utc)
        );

        BlueprintTrack subtitle = encode.Tracks.Single(t => t.SourceStreamIndex == SubtitleIndex);
        subtitle.Files.Should().ContainSingle().Which.Should().Be(preserved);
    }

    [Fact]
    public void EverySourceStream_AppearsExactlyOnce_TheCompletenessInvariant()
    {
        BlueprintEncode encode = BuildEncode();

        encode.Tracks.Should().HaveCount(4);
        encode
            .Tracks.Select(t => t.SourceStreamIndex)
            .Should()
            .BeEquivalentTo([VideoIndex, TranscodedAudioIndex, DroppedAudioIndex, SubtitleIndex]);
    }

    [Fact]
    public void CopiedVideo_IsLosslessAndReconstructable_WithItsRealRenditionFiles()
    {
        BlueprintEncode encode = BuildEncode();

        BlueprintTrack video = encode.Tracks.Single(t => t.SourceStreamIndex == VideoIndex);
        video.Kind.Should().Be("video");
        video.Policy.Should().Be("copy");
        video.Fidelity.Should().Be("lossless");
        video.Reconstructable.Should().BeTrue();
        video.OriginalParams.Should().BeNull();
        video.Files.Should().NotBeEmpty();
        video.Files.Should().OnlyContain(f => f.StartsWith("video_1920x1080_SDR/"));
    }

    [Fact]
    public void TranscodedAudio_IsLossyAndNotReconstructable_AndRecordsOriginalParams()
    {
        BlueprintEncode encode = BuildEncode();

        BlueprintTrack audio = encode.Tracks.Single(t =>
            t.SourceStreamIndex == TranscodedAudioIndex
        );
        audio.Kind.Should().Be("audio");
        audio.Policy.Should().Be("transcode");
        audio.Fidelity.Should().Be("lossy");
        audio.Reconstructable.Should().BeFalse();
        audio.OriginalParams.Should().NotBeNull();
        ((string?)audio.OriginalParams!["codec"]).Should().Be("flac");
        ((int?)audio.OriginalParams!["channels"]).Should().Be(2);
        ((int?)audio.OriginalParams!["sample_rate"]).Should().Be(48_000);
        audio.Files.Should().OnlyContain(f => f.StartsWith("audio_jpn_opus/"));
    }

    [Fact]
    public void DroppedAudioStream_SurfacesAsADroppedTrack_NeverSilentlyOmitted()
    {
        BlueprintEncode encode = BuildEncode();

        BlueprintTrack dropped = encode.Tracks.Single(t =>
            t.SourceStreamIndex == DroppedAudioIndex
        );
        dropped.Kind.Should().Be("audio");
        dropped.SourceCodec.Should().Be("aac");
        dropped.Policy.Should().Be("dropped");
        dropped.Fidelity.Should().Be("lost");
        dropped.Reconstructable.Should().BeFalse();
        dropped.Files.Should().BeEmpty();
    }

    [Fact]
    public void PreservedBitmapSubtitle_PointsAtTheMksOnly_NeverTheOcrVtt()
    {
        BlueprintEncode encode = BuildEncode();

        BlueprintTrack subtitle = encode.Tracks.Single(t => t.SourceStreamIndex == SubtitleIndex);
        subtitle.Kind.Should().Be("subtitle");
        subtitle.Policy.Should().Be("extract");
        subtitle.Fidelity.Should().Be("lossless");
        subtitle.Reconstructable.Should().BeTrue();
        subtitle.Container.Should().Be("mks");
        subtitle.Files.Should().ContainSingle();
        subtitle.Files[0].Should().EndWith(".mks");
        subtitle.Files.Should().NotContain(f => f.EndsWith(".vtt"));
    }

    [Fact]
    public void LossyWarnings_ContainOneEntryPerLossyIrreversibleOrDroppedTrack()
    {
        BlueprintEncode encode = BuildEncode();

        // Transcoded audio + dropped audio are lossy/lost. Copy video and
        // extracted subtitle are lossless and must NOT produce a warning.
        encode.LossyWarnings.Should().HaveCount(2);
        encode.LossyWarnings.Should().Contain(w => w.StartsWith($"audio[{TranscodedAudioIndex}]"));
        encode.LossyWarnings.Should().Contain(w => w.StartsWith($"audio[{DroppedAudioIndex}]"));
    }

    [Fact]
    public void ReconstructionCommandTemplate_IsGeneratedFromTheTrackMapping_NotAStaticStub()
    {
        BlueprintEncode encode = BuildEncode();

        encode.ReconstructionCommandTemplate.Should().NotBeNullOrWhiteSpace();
        encode.ReconstructionCommandTemplate.Should().StartWith("ffmpeg ");
        // One -i per track that actually retained a file — the dropped audio
        // stream contributed no file and must not appear as an input.
        encode.ReconstructionCommandTemplate.Should().Contain("-i \"video_1920x1080_SDR/");
        encode.ReconstructionCommandTemplate.Should().Contain("-i \"audio_jpn_opus/");
        encode.ReconstructionCommandTemplate.Should().Contain("-i \"subtitles/");
        // "matroska" (the mapped source container) has "mkv" as its real
        // reconstruction file extension — mirrors OutputNamingResolver's own
        // Container -> extension mapping for the single-file MKV case.
        encode.ReconstructionCommandTemplate.Should().Contain("reconstructed.mkv");
    }

    [Fact]
    public void TargetContainer_IsTheSourceContainer_MappedFromFfprobeFormatName()
    {
        BlueprintEncode encode = BuildEncode();

        // Source format_name is "matroska,webm" — never the HLS/fMP4 output.
        encode.TargetContainer.Should().Be("matroska");
    }

    [Fact]
    public void EncodeMetadata_CarriesThroughFromTheCaller()
    {
        BlueprintEncode encode = BuildEncode();

        encode.PresetSlug.Should().Be("anime-1080p");
        encode.PresetId.Should().Be("01HZPRESET");
        encode.ProfileFingerprint.Should().Be("fingerprint-abc");
        encode.EncoderVersion.Should().Be("1.2.3");
        encode.OutputLocation.Should().Be("Anime/Show/Season 01/Show S01E01");
    }
}
