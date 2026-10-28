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
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Output;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Output;

/// <summary>
/// The scenario a plan-based reconciliation cannot cover: a single preset
/// the decode-aware bundler split into several self-finalizing bundles, so
/// there is no in-memory <c>OutputPlan</c> anywhere that describes every
/// rendition — only the destination directory does. Exercises
/// <see cref="HlsOnDiskPlanReconstructor"/> straight into
/// <see cref="HlsOutputStrategy.FinalizeAsync"/> exactly as
/// <c>VideoEncodeJob.ReconcileMasterPlaylistAsync</c> now does, with no plan
/// in play at all — this is the single-preset / self-finalized-bundles path
/// the earlier plan-based fix never actually reached.
/// </summary>
public partial class HlsOnDiskPlanReconstructorTests : IDisposable
{
    [GeneratedRegex("#EXT-X-MEDIA:TYPE=AUDIO", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AudioMediaEntry();

    [GeneratedRegex(
        "#EXT-X-MEDIA:TYPE=SUBTITLES",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SubtitleMediaEntry();

    private readonly string _outputDirectory;
    private readonly Mock<IMediaAnalyzer> _mediaAnalyzer = new();

    public HlsOnDiskPlanReconstructorTests()
    {
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nomercy-disk-union-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, true);
    }

    [Fact]
    public async Task ReconstructAndFinalize_NoPlanInvolved_ProducesCompleteMasterMatchingDisk()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        // Published output of a single preset the decode-aware bundler split
        // into two self-finalizing Whole bundles — video/audio/subtitles were
        // never in the SAME in-memory OutputPlan at any point in this test.
        WriteVariant("video_3840x2160", "video_3840x2160", segmentBytes: 900_000);
        WriteVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        WriteVariant("audio_eng_eac3", "audio_eng_eac3", segmentBytes: 60_000);
        WriteInitMp4("video_3840x2160");
        WriteInitMp4("video_1920x1080_SDR");
        WriteSubtitle("eng", "full");

        SetupProbe(
            "video_3840x2160",
            codec: "hevc",
            width: 3840,
            height: 2160,
            bitDepth: 10,
            colorTransfer: "smpte2084"
        );
        SetupProbe(
            "video_1920x1080_SDR",
            codec: "hevc",
            width: 1920,
            height: 1080,
            bitDepth: 8,
            colorTransfer: "bt709"
        );

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        HlsOutputStrategy strategy = new(storage);
        await strategy.FinalizeAsync(_outputDirectory, plan, "Title", CancellationToken.None);

        string master = await File.ReadAllTextAsync(Path.Combine(_outputDirectory, "Title.m3u8"));

        List<string> streamInfLines =
        [
            .. master
                .Split('\n')
                .Where(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)),
        ];
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

        // Populated audio group — not a dangling AUDIO="..." referencing a
        // group with zero EXT-X-MEDIA entries.
        hdrLine.Should().Contain("AUDIO=\"audio_eac3\"");
        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_eac3\",LANGUAGE=\"eng\"");

        master.Should().Contain("#EXT-X-MEDIA:TYPE=SUBTITLES");
        hdrLine.Should().Contain("SUBTITLES=\"subs\"");

        // Renditions-on-disk == entries-in-master, for every kind.
        int videoDirsOnDisk = Directory.GetDirectories(_outputDirectory, "video_*").Length;
        int audioDirsOnDisk = Directory.GetDirectories(_outputDirectory, "audio_*").Length;
        int subtitleTracksOnDisk = Directory
            .GetDirectories(Path.Combine(_outputDirectory, "subtitles"))
            .Sum(languageDir => Directory.GetFiles(languageDir, "*.ass").Length);

        int videoEntriesInMaster = streamInfLines.Count;
        int audioEntriesInMaster = AudioMediaEntry().Matches(master).Count;
        int subtitleEntriesInMaster = SubtitleMediaEntry().Matches(master).Count;

        videoEntriesInMaster.Should().Be(videoDirsOnDisk);
        audioEntriesInMaster.Should().Be(audioDirsOnDisk);
        subtitleEntriesInMaster.Should().Be(subtitleTracksOnDisk);
    }

    private void SetupProbe(
        string dirName,
        string codec,
        int width,
        int height,
        int bitDepth,
        string colorTransfer,
        string? pixelFormat = null
    )
    {
        MediaInfo info = new(
            FilePath: dirName,
            Format: "mov,mp4,m4a,3gp,3g2,mj2",
            Duration: TimeSpan.FromSeconds(6),
            OverallBitRateKbps: 0,
            FileSizeBytes: 0,
            VideoStreams:
            [
                new VideoStreamInfo(
                    Index: 0,
                    Codec: codec,
                    Width: width,
                    Height: height,
                    FrameRate: 23.976,
                    BitDepth: bitDepth,
                    PixelFormat: pixelFormat ?? (bitDepth >= 10 ? "yuv420p10le" : "yuv420p"),
                    ColorPrimaries: "bt2020",
                    ColorTransfer: colorTransfer,
                    ColorSpace: "bt2020nc",
                    IsDefault: true,
                    BitRateKbps: 0
                ),
            ],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: []
        );

        _mediaAnalyzer
            .Setup(analyzer =>
                analyzer.AnalyzeAsync(
                    It.Is<string>(path => path.Contains(dirName)),
                    It.IsAny<IStorage>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(info);
    }

    /// <summary>
    /// A 4:4:4 rendition reconstructed from disk must reach the master as Range
    /// Extensions. The reconstruction probes the real stream but used to discard
    /// its pixel format and assume 4:2:0, so a rung no handset can decode was
    /// advertised as Main10 — a profile every handset claims. Clients selected it
    /// and died at the decoder instead of passing over it.
    /// </summary>
    [Fact]
    public async Task Reconstruct_FourFourFourRendition_IsAdvertisedAsRangeExtensions()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteVariant("video_3840x1600", "video_3840x1600", segmentBytes: 900_000);
        WriteVariant("video_1920x800_SDR", "video_1920x800_SDR", segmentBytes: 300_000);
        WriteVariant("audio_eng_eac3", "audio_eng_eac3", segmentBytes: 60_000);

        SetupProbe(
            "video_3840x1600",
            codec: "hevc",
            width: 3840,
            height: 1600,
            bitDepth: 10,
            colorTransfer: "smpte2084",
            pixelFormat: "yuv444p10le"
        );
        SetupProbe(
            "video_1920x800_SDR",
            codec: "h264",
            width: 1920,
            height: 800,
            bitDepth: 8,
            colorTransfer: "bt709"
        );

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        HlsOutputStrategy strategy = new(storage);
        await strategy.FinalizeAsync(_outputDirectory, plan, "Title", CancellationToken.None);

        string master = await File.ReadAllTextAsync(Path.Combine(_outputDirectory, "Title.m3u8"));

        string? hdrLine = master
            .Split('\n')
            .FirstOrDefault(line =>
                line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)
                && line.Contains("RESOLUTION=3840x1600", StringComparison.Ordinal)
            );

        hdrLine.Should().NotBeNull();
        hdrLine.Should().Contain("hvc1.4.10.");
        hdrLine.Should().NotContain("avc1");
        hdrLine.Should().NotContain("hvc1.2.");
    }

    private static int ExtractInt(string streamInfLine, string attribute)
    {
        System.Text.RegularExpressions.Match match = Regex.Match(
            streamInfLine,
            $@"{attribute}=(?<value>\d+)",
            RegexOptions.None,
            TimeSpan.FromSeconds(1)
        );
        return int.Parse(match.Groups["value"].Value);
    }

    private void WriteVariant(string subDirectory, string name, int segmentBytes)
    {
        string variantDirectory = Path.Combine(_outputDirectory, subDirectory);
        Directory.CreateDirectory(variantDirectory);

        byte[] segment = new byte[segmentBytes];
        File.WriteAllBytes(Path.Combine(variantDirectory, $"{name}_00000.m4s"), segment);

        string playlist = $"#EXTM3U\n#EXTINF:6.000000,\n{name}_00000.m4s\n#EXT-X-ENDLIST\n";
        File.WriteAllText(Path.Combine(variantDirectory, $"{name}.m3u8"), playlist);
    }

    private void WriteInitMp4(string subDirectory)
    {
        string variantDirectory = Path.Combine(_outputDirectory, subDirectory);
        File.WriteAllBytes(Path.Combine(variantDirectory, "init.mp4"), new byte[512]);
    }

    private void WriteSubtitle(string language, string variant)
    {
        string subtitleDirectory = Path.Combine(_outputDirectory, "subtitles", language);
        Directory.CreateDirectory(subtitleDirectory);
        File.WriteAllText(Path.Combine(subtitleDirectory, $"{variant}.ass"), "[Script Info]\n");
    }
}
