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

using System.Text.RegularExpressions;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Output;

/// <summary>
/// A title encoded as a sequential cascade of independent preset bundles
/// (e.g. a "4K HDR" bundle first, then a "1080p SDR" bundle as a separate
/// job) — or, equivalently, a single coordinated run the decode-aware
/// bundler split into several self-finalizing <c>Whole</c> bundles — has
/// each bundle write the master playlist from only its own narrow
/// <see cref="OutputPlan"/>. The later bundle's publish overwrites the
/// master, so the earlier bundle's video/audio/subtitle renditions
/// disappear from it even though their segments are still on disk.
///
/// <see cref="NarrowPerBundlePlan_OrphansEarlierBundlesRenditions"/> pins
/// down that defect mechanism directly against
/// <see cref="HlsOutputStrategy.FinalizeAsync"/> — it is the reason the
/// fix (NoMercy.MediaProcessing's <c>VideoEncodeJob.ReconcileMasterPlaylistAsync</c>)
/// must never call FinalizeAsync with a single bundle's narrow plan once
/// more than one bundle has published against the same output directory.
///
/// <see cref="UnionedPlan_ProducesCompleteMasterWithNoOrphans"/> proves the
/// fix's actual approach: calling FinalizeAsync ONCE with the full merged
/// plan (every video/audio/subtitle rendition across every bundle) produces
/// a complete master, with distinct per-resolution bandwidth, correct HEVC
/// levels, correct VIDEO-RANGE, and every audio/subtitle track intact.
/// </summary>
public class HlsMasterPlaylistCascadeUnionTests : IDisposable
{
    private readonly string _outputDirectory;

    public HlsMasterPlaylistCascadeUnionTests()
    {
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nomercy-cascade-union-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, true);
    }

    [Fact]
    public async Task NarrowPerBundlePlan_OrphansEarlierBundlesRenditions()
    {
        HlsOutputStrategy strategy = new(TestStorageFactory.CreateLocal());

        // Bundle 1 ("4K HDR"): produces the 4K video rendition plus the
        // audio and subtitle tracks — the smart orchestrator shares those
        // once across the whole ladder instead of duplicating them per rung.
        WriteVariant("video_3840x2160", "video_3840x2160", segmentBytes: 900_000);
        WriteVariant("audio_eng_eac3", "audio_eng_eac3", segmentBytes: 60_000);
        WriteVariant("audio_jpn_eac3", "audio_jpn_eac3", segmentBytes: 60_000);
        WriteSubtitle("eng", "full");

        OutputPlan bundle1Plan = BuildPlan(
            videoOutputs: [Create4KHdrVideo()],
            audioOutputs: [CreateAudio("eng"), CreateAudio("jpn")],
            subtitleOutputs: [CreateSubtitle()]
        );

        await strategy.FinalizeAsync(
            _outputDirectory,
            bundle1Plan,
            "Title",
            CancellationToken.None
        );

        string masterAfterBundle1 = await File.ReadAllTextAsync(MasterPath);
        masterAfterBundle1.Should().Contain("3840x2160");
        masterAfterBundle1.Should().Contain("TYPE=AUDIO");
        masterAfterBundle1.Should().Contain("TYPE=SUBTITLES");

        // Bundle 2 ("1080p SDR"): a separate job, publishing its own video
        // rendition only — its OWN OutputPlan carries no audio and no
        // subtitle outputs at all, because those were bundle 1's job.
        WriteVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);

        OutputPlan bundle2Plan = BuildPlan(
            videoOutputs: [Create1080pSdrVideo()],
            audioOutputs: [],
            subtitleOutputs: []
        );

        await strategy.FinalizeAsync(
            _outputDirectory,
            bundle2Plan,
            "Title",
            CancellationToken.None
        );

        string masterAfterBundle2 = await File.ReadAllTextAsync(MasterPath);

        // The defect: bundle 2's narrow plan overwrote the master, orphaning
        // everything bundle 1 published even though it is still on disk.
        masterAfterBundle2.Should().NotContain("3840x2160");
        masterAfterBundle2.Should().NotContain("TYPE=AUDIO");
        masterAfterBundle2.Should().NotContain("TYPE=SUBTITLES");
        masterAfterBundle2.Should().Contain("1920x1080");
    }

    [Fact]
    public async Task UnionedPlan_ProducesCompleteMasterWithNoOrphans()
    {
        HlsOutputStrategy strategy = new(TestStorageFactory.CreateLocal());

        WriteVariant("video_3840x2160", "video_3840x2160", segmentBytes: 900_000);
        WriteVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        WriteVariant("audio_eng_eac3", "audio_eng_eac3", segmentBytes: 60_000);
        WriteVariant("audio_jpn_eac3", "audio_jpn_eac3", segmentBytes: 60_000);
        WriteSubtitle("eng", "full");

        // What NoMercy.MediaProcessing.VideoEncodeJob.ReconcileMasterPlaylistAsync
        // now builds via IEncodingOrchestrator.PlanMergedAsync before calling
        // FinalizeAsync — the union of every bundle's video/audio/subtitle
        // outputs, not just the last bundle's own slice.
        OutputPlan unionedPlan = BuildPlan(
            videoOutputs: [Create4KHdrVideo(), Create1080pSdrVideo()],
            audioOutputs: [CreateAudio("eng"), CreateAudio("jpn")],
            subtitleOutputs: [CreateSubtitle()]
        );

        await strategy.FinalizeAsync(
            _outputDirectory,
            unionedPlan,
            "Title",
            CancellationToken.None
        );

        string master = await File.ReadAllTextAsync(MasterPath);

        List<string> streamInfLines = master
            .Split('\n')
            .Where(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            .ToList();
        streamInfLines.Should().HaveCount(2);

        string? hdrLine = streamInfLines.FirstOrDefault(line =>
            line.Contains("RESOLUTION=3840x2160", StringComparison.Ordinal)
        );
        string? sdrLine = streamInfLines.FirstOrDefault(line =>
            line.Contains("RESOLUTION=1920x1080", StringComparison.Ordinal)
        );
        hdrLine.Should().NotBeNull();
        sdrLine.Should().NotBeNull();

        hdrLine.Should().Contain("VIDEO-RANGE=PQ");
        sdrLine.Should().Contain("VIDEO-RANGE=SDR");

        hdrLine.Should().MatchRegex(@"CODECS=""hvc1\.2\.4\.L150\.B0,ec-3""");
        sdrLine.Should().MatchRegex(@"CODECS=""hvc1\.1\.6\.L120\.B0,ec-3""");

        int hdrBandwidth = ExtractInt(hdrLine!, "BANDWIDTH");
        int sdrBandwidth = ExtractInt(sdrLine!, "BANDWIDTH");
        hdrBandwidth.Should().NotBe(sdrBandwidth);
        hdrBandwidth.Should().BeGreaterThan(sdrBandwidth);

        master
            .Should()
            .Contain(
                "TYPE=AUDIO,GROUP-ID=\"audio_eac3\",LANGUAGE=\"eng\"",
                "the English audio rendition must still be referenced"
            );
        master
            .Should()
            .Contain(
                "TYPE=AUDIO,GROUP-ID=\"audio_eac3\",LANGUAGE=\"jpn\"",
                "the Japanese audio rendition must still be referenced"
            );
        master.Should().Contain("TYPE=SUBTITLES");

        // Zero orphans: every video_*/audio_* rendition directory and the
        // subtitle sidecar on disk is referenced somewhere in the master.
        master.Should().Contain("video_3840x2160/video_3840x2160.m3u8");
        master.Should().Contain("video_1920x1080_SDR/video_1920x1080_SDR.m3u8");
        master.Should().Contain("audio_eng_eac3/audio_eng_eac3.m3u8");
        master.Should().Contain("audio_jpn_eac3/audio_jpn_eac3.m3u8");
        master.Should().Contain("subtitles/eng/full.ass");
    }

    private string MasterPath => Path.Combine(_outputDirectory, "Title.m3u8");

    private static int ExtractInt(string streamInfLine, string attribute)
    {
        Match match = Regex.Match(
            streamInfLine,
            $@"{attribute}=(?<value>\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        return int.Parse(match.Groups["value"].Value);
    }

    private static OutputPlan BuildPlan(
        VideoOutputPlan[] videoOutputs,
        AudioOutputPlan[] audioOutputs,
        SubtitleOutputPlan[] subtitleOutputs
    ) =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs: videoOutputs,
            AudioOutputs: audioOutputs,
            SubtitleOutputs: subtitleOutputs,
            Thumbnails: null
        );

    private static VideoOutputPlan Create4KHdrVideo() =>
        new(
            Width: 3840,
            Height: 2160,
            EncoderName: "libx265",
            Crf: 18,
            BitrateKbps: 0,
            Preset: "slow",
            Profile: "main10",
            Level: null,
            TenBit: true,
            PixelFormat: "yuv420p10le",
            MapLabel: "[v0]",
            ExtraFlags: new(),
            FrameRate: 23.976,
            SegmentNameTemplate: "video_{label}/video_{label}",
            PlaylistNameTemplate: "video_{label}/video_{label}",
            IsHdrOutput: true
        );

    private static VideoOutputPlan Create1080pSdrVideo() =>
        new(
            Width: 1920,
            Height: 1080,
            EncoderName: "libx265",
            Crf: 20,
            BitrateKbps: 0,
            Preset: "slow",
            Profile: "main",
            Level: null,
            TenBit: false,
            PixelFormat: "yuv420p",
            MapLabel: "[v0]",
            ExtraFlags: new(),
            FrameRate: 23.976,
            SegmentNameTemplate: "video_{label}/video_{label}",
            PlaylistNameTemplate: "video_{label}/video_{label}",
            IsHdrOutput: false
        );

    private static AudioOutputPlan CreateAudio(string language) =>
        new(
            EncoderName: "eac3",
            BitrateKbps: 640,
            Channels: 6,
            SampleRate: 48000,
            Action: StreamAction.Transcode,
            Language: language,
            MapLabel: language == "eng" ? "0:a:0" : "0:a:1",
            SegmentNameTemplate: "audio_{lang}_{codec}/audio_{lang}_{codec}",
            PlaylistNameTemplate: "audio_{lang}_{codec}/audio_{lang}_{codec}"
        );

    private static SubtitleOutputPlan CreateSubtitle() =>
        new(
            OutputCodec: SubtitleCodecType.Ass,
            Action: StreamAction.Extract,
            Language: "eng",
            SourceIndex: 0,
            MapLabel: null
        );

    private void WriteVariant(string subDirectory, string name, int segmentBytes)
    {
        string variantDirectory = Path.Combine(_outputDirectory, subDirectory);
        Directory.CreateDirectory(variantDirectory);

        byte[] segment = new byte[segmentBytes];
        File.WriteAllBytes(Path.Combine(variantDirectory, $"{name}_00000.ts"), segment);

        string playlist = $"#EXTM3U\n#EXTINF:6.000000,\n{name}_00000.ts\n#EXT-X-ENDLIST\n";
        File.WriteAllText(Path.Combine(variantDirectory, $"{name}.m3u8"), playlist);
    }

    private void WriteSubtitle(string language, string variant)
    {
        string subtitleDirectory = Path.Combine(_outputDirectory, "subtitles", language);
        Directory.CreateDirectory(subtitleDirectory);
        File.WriteAllText(Path.Combine(subtitleDirectory, $"{variant}.ass"), "[Script Info]\n");
    }
}
