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

using System.Reflection;
using System.Text.RegularExpressions;
using Moq;
using Newtonsoft.Json;
using NoMercy.Database.Models.Media;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Bundle;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// The master rebuild moved OUT of the encode-finalize path (fragile scope: a
/// preset the decode-aware bundler split into several self-finalizing bundles
/// only ever saw its own last rendition there) and INTO the scan
/// (<see cref="FileManager.MakeMetadata"/> via the extracted
/// <c>RebuildHlsMasterFromDiskAsync</c>), because a completed scan reliably
/// walks the fully-published media root — every rendition every bundle ever
/// wrote, not just the last one. This exercises that extracted method
/// directly against a fixture published output folder, exactly like a
/// real post-encode rescan would see it.
/// </summary>
public sealed partial class FileManagerMasterRebuildTests : IDisposable
{
    [GeneratedRegex("#EXT-X-MEDIA:TYPE=AUDIO", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AudioMediaEntry();

    [GeneratedRegex(
        "#EXT-X-MEDIA:TYPE=SUBTITLES",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SubtitleMediaEntry();

    [GeneratedRegex("AUDIO=\"(?<id>[^\"]+)\"", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AudioGroupAttribute();

    private readonly string _tempRoot;
    private readonly Mock<IMediaAnalyzer> _mediaAnalyzer = new();

    public FileManagerMasterRebuildTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), $"nm-master-rebuild-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot))
            Directory.Delete(_tempRoot, recursive: true);
    }

    [Fact]
    public async Task Scan_PublishedCascadeOutput_RebuildsCompleteMasterFromDisk()
    {
        string hostDir = Path.Combine(_tempRoot, "The.Punisher.One.Last.Kill.(2026).NoMercy");
        Directory.CreateDirectory(hostDir);

        WriteVariant(hostDir, "video_3840x2160", segmentBytes: 900_000, extension: ".m4s");
        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteVariant(hostDir, "audio_eng_eac3", segmentBytes: 60_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_3840x2160");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        WriteSubtitle(hostDir, "eng", "full");

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

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));

        FileManager manager = BuildFileManager();
        List<IVideo> video = InvokeGetVideoHashList(manager, storage, hostDir);
        video.Should().HaveCount(2, "both rendition dirs must be picked up by the scan");

        string fileName = "/The.Punisher.One.Last.Kill.(2026).NoMercy.m3u8";

        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, video);

        string masterPath = Path.Combine(hostDir, "The.Punisher.One.Last.Kill.(2026).NoMercy.m3u8");
        File.Exists(masterPath)
            .Should()
            .BeTrue("the rebuild must write the master onto the host folder");

        string master = await File.ReadAllTextAsync(masterPath);

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

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio_eac3\",LANGUAGE=\"eng\"");
        master.Should().Contain("#EXT-X-MEDIA:TYPE=SUBTITLES");
        hdrLine.Should().Contain("AUDIO=\"audio_eac3\"");
        hdrLine.Should().Contain("SUBTITLES=\"subs\"");

        // Renditions-on-disk == entries-in-master, for every kind.
        int videoDirsOnDisk = Directory.GetDirectories(hostDir, "video_*").Length;
        int audioDirsOnDisk = Directory.GetDirectories(hostDir, "audio_*").Length;
        int subtitleTracksOnDisk = Directory
            .GetDirectories(Path.Combine(hostDir, "subtitles"))
            .Sum(languageDir => Directory.GetFiles(languageDir, "*.ass").Length);

        streamInfLines.Count.Should().Be(videoDirsOnDisk);
        AudioMediaEntry().Matches(master).Count.Should().Be(audioDirsOnDisk);
        SubtitleMediaEntry().Matches(master).Count.Should().Be(subtitleTracksOnDisk);
    }

    [Fact]
    public async Task Scan_SecondPass_MasterAlreadyComplete_SkipsReprobeAndKeepsMaster()
    {
        string hostDir = Path.Combine(_tempRoot, "Repeat.Run.(2024).NoMercy");
        Directory.CreateDirectory(hostDir);
        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        WriteVariant(hostDir, "audio_eng_eac3", segmentBytes: 60_000, extension: ".m4s");
        SetupProbe("video_1920x1080_SDR", "hevc", 1920, 1080, bitDepth: 8, colorTransfer: "bt709");

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();
        string fileName = "/Repeat.Run.(2024).NoMercy.m3u8";
        string masterPath = Path.Combine(hostDir, "Repeat.Run.(2024).NoMercy.m3u8");

        List<IVideo> firstVideo = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, firstVideo);

        _mediaAnalyzer
            .Invocations.Count.Should()
            .BeGreaterThan(
                0,
                "the first pass has no master on disk and must probe every rendition"
            );
        string firstMaster = await File.ReadAllTextAsync(masterPath);

        // A second scan of the same, already-complete output must be a pure read:
        // no rendition probe, no master rewrite — this is the per-file cost that
        // made a rescan crawl.
        _mediaAnalyzer.Invocations.Clear();
        List<IVideo> secondVideo = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, secondVideo);

        _mediaAnalyzer
            .Invocations.Should()
            .BeEmpty("a master already advertising every on-disk rendition must not be reprobed");
        (await File.ReadAllTextAsync(masterPath))
            .Should()
            .Be(firstMaster, "the skipped rebuild must leave the master untouched");
    }

    [Fact]
    public async Task Scan_SecondPass_NewLadderRungPublished_RecreatesMasterWithEveryRung()
    {
        string hostDir = Path.Combine(_tempRoot, "Cascade.Grows.(2025).NoMercy");
        Directory.CreateDirectory(hostDir);
        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        WriteVariant(hostDir, "audio_eng_eac3", segmentBytes: 60_000, extension: ".m4s");
        SetupProbe("video_1920x1080_SDR", "hevc", 1920, 1080, bitDepth: 8, colorTransfer: "bt709");

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();
        string fileName = "/Cascade.Grows.(2025).NoMercy.m3u8";
        string masterPath = Path.Combine(hostDir, "Cascade.Grows.(2025).NoMercy.m3u8");

        List<IVideo> firstVideo = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, firstVideo);
        CountStreamInf(await File.ReadAllTextAsync(masterPath))
            .Should()
            .Be(1, "the first bundle published only the 1080p rung");

        // A later self-finalizing bundle publishes the 4K rung into the SAME output
        // folder — the exact multi-run case the disk-truth rebuild exists for. The
        // skip guard must NOT treat the now-stale master as complete.
        WriteVariant(hostDir, "video_3840x2160", segmentBytes: 900_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_3840x2160");
        SetupProbe("video_3840x2160", "hevc", 3840, 2160, bitDepth: 10, colorTransfer: "smpte2084");

        List<IVideo> secondVideo = InvokeGetVideoHashList(manager, storage, hostDir);
        secondVideo.Should().HaveCount(2, "the scan sees both published rungs on disk");
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, secondVideo);

        string secondMaster = await File.ReadAllTextAsync(masterPath);
        CountStreamInf(secondMaster)
            .Should()
            .Be(2, "a master missing an on-disk rung must be recreated to list every rung");
        secondMaster.Should().Contain("RESOLUTION=3840x2160");
        secondMaster.Should().Contain("RESOLUTION=1920x1080");
    }

    [Fact]
    public async Task Scan_NonHlsItem_NoVideoRenditions_DoesNotWriteAMaster()
    {
        string hostDir = Path.Combine(_tempRoot, "Some.Raw.File.(2020).NoMercy");
        Directory.CreateDirectory(hostDir);

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();

        await InvokeRebuildHlsMasterFromDiskAsync(
            manager,
            storage,
            hostDir,
            "/Some.Raw.File.(2020).NoMercy.mkv",
            []
        );

        Directory.GetFiles(hostDir, "*.m3u8").Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Regression: the audio-loss bug (ebc82df9's rebuild dropping the audio
    // group for older-encoded titles) and its blueprint-primary fix.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Scan_OldNamingAudioDirsNoCodecSuffix_NoBlueprint_RebuildsMasterWithAudioGroup()
    {
        string hostDir = Path.Combine(_tempRoot, "Chainsaw.Man.S01E01.(2022).NoMercy");
        Directory.CreateDirectory(hostDir);

        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        // Pre-codec-suffix encoder naming: no `_<codec>` token on the dir at
        // all — the exact on-disk shape that regressed audio to silent.
        WriteVariant(hostDir, "audio_jpn", segmentBytes: 60_000, extension: ".m4s");
        WriteVariant(hostDir, "audio_eng", segmentBytes: 60_000, extension: ".m4s");

        SetupProbe("video_1920x1080_SDR", "hevc", 1920, 1080, bitDepth: 8, colorTransfer: "bt709");

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();
        string fileName = "/Chainsaw.Man.S01E01.(2022).NoMercy.m3u8";
        string masterPath = Path.Combine(hostDir, "Chainsaw.Man.S01E01.(2022).NoMercy.m3u8");

        List<IVideo> video = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, video);

        File.Exists(masterPath).Should().BeTrue();
        string master = await File.ReadAllTextAsync(masterPath);

        AudioMediaEntry()
            .Matches(master)
            .Count.Should()
            .Be(2, "both old-naming audio dirs must surface as a group, not be silently skipped");
        master.Should().Contain("LANGUAGE=\"jpn\"");
        master.Should().Contain("LANGUAGE=\"eng\"");

        string streamInfLine = master
            .Split('\n')
            .First(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));
        System.Text.RegularExpressions.Match audioAttr = AudioGroupAttribute().Match(streamInfLine);
        audioAttr
            .Success.Should()
            .BeTrue(
                "the video variant must reference the audio group the rebuild actually emitted"
            );
        master.Should().Contain($"GROUP-ID=\"{audioAttr.Groups["id"].Value}\"");
    }

    [Fact]
    public async Task Scan_BlueprintPresent_CustomAudioRenditionPath_MasterBuiltFromBlueprintFiles()
    {
        string hostDir = Path.Combine(_tempRoot, "Custom.Naming.Title.(2025).NoMercy");
        Directory.CreateDirectory(hostDir);

        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        SetupProbe("video_1920x1080_SDR", "hevc", 1920, 1080, bitDepth: 8, colorTransfer: "bt709");

        // A custom PlaylistNameTemplate produced this — no "audio_" prefix at
        // all. A directory-name parse (even a lenient one) can never resolve
        // this; only the blueprint's recorded Files[] can.
        string customAudioDir = Path.Combine(hostDir, "sound", "japanese-track");
        Directory.CreateDirectory(customAudioDir);
        File.WriteAllBytes(Path.Combine(customAudioDir, "stream_00000.m4s"), new byte[60_000]);
        File.WriteAllText(
            Path.Combine(customAudioDir, "stream.m3u8"),
            "#EXTM3U\n#EXTINF:6.000000,\nstream_00000.m4s\n#EXT-X-ENDLIST\n"
        );

        WriteAudioBlueprint(
            hostDir,
            [
                new BlueprintTrack(
                    SourceStreamIndex: 1,
                    Kind: "audio",
                    SourceCodec: "aac",
                    SourceLanguage: "jpn",
                    Policy: "copy",
                    Fidelity: "lossless",
                    Reconstructable: true,
                    OriginalParams: null,
                    Container: "hls",
                    Files:
                    [
                        "sound/japanese-track/stream.m3u8",
                        "sound/japanese-track/stream_00000.m4s",
                    ],
                    Sha256: null
                ),
            ]
        );

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();
        string fileName = "/Custom.Naming.Title.(2025).NoMercy.m3u8";
        string masterPath = Path.Combine(hostDir, "Custom.Naming.Title.(2025).NoMercy.m3u8");

        List<IVideo> video = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, video);

        string master = await File.ReadAllTextAsync(masterPath);

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().Contain("LANGUAGE=\"jpn\"");
        master
            .Should()
            .Contain(
                "URI=\"sound/japanese-track/stream.m3u8\"",
                "the master must reference the blueprint's resolved output path, not a guessed directory name"
            );
    }

    [Fact]
    public async Task Scan_ZeroAudioRenditions_MasterHasNoAudioGroupAndNoDanglingReference()
    {
        string hostDir = Path.Combine(_tempRoot, "Silent.Documentary.(2019).NoMercy");
        Directory.CreateDirectory(hostDir);

        WriteVariant(hostDir, "video_1920x1080_SDR", segmentBytes: 300_000, extension: ".m4s");
        WriteInitMp4(hostDir, "video_1920x1080_SDR");
        SetupProbe("video_1920x1080_SDR", "hevc", 1920, 1080, bitDepth: 8, colorTransfer: "bt709");

        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new StoragePathGuard([], driver));
        FileManager manager = BuildFileManager();
        string fileName = "/Silent.Documentary.(2019).NoMercy.m3u8";
        string masterPath = Path.Combine(hostDir, "Silent.Documentary.(2019).NoMercy.m3u8");

        List<IVideo> video = InvokeGetVideoHashList(manager, storage, hostDir);
        await InvokeRebuildHlsMasterFromDiskAsync(manager, storage, hostDir, fileName, video);

        string master = await File.ReadAllTextAsync(masterPath);

        master.Should().NotContain("#EXT-X-MEDIA:TYPE=AUDIO");
        master
            .Should()
            .NotContain(
                "AUDIO=\"",
                "a title with zero audio renditions must not reference an audio group that was never emitted"
            );
    }

    private static void WriteAudioBlueprint(string hostDir, IReadOnlyList<BlueprintTrack> tracks)
    {
        MediaBlueprint blueprint = new(
            Version: 1,
            Identity: new BlueprintIdentity(
                Type: "movie",
                TmdbId: 1,
                Show: null,
                Season: null,
                Episode: null,
                Title: "Fixture",
                Year: 2025
            ),
            Source: new BlueprintSource(
                Path: "/source/fixture.mkv",
                Filename: "fixture.mkv",
                Container: "matroska",
                SizeBytes: 0,
                DurationSeconds: 0,
                Sha256: null,
                Ffprobe: null
            ),
            Encodes:
            [
                new BlueprintEncode(
                    PresetSlug: "fixture-preset",
                    PresetId: "1",
                    ProfileFingerprint: null,
                    EncoderVersion: "test",
                    TargetContainer: "hls",
                    OutputLocation: hostDir,
                    CreatedAt: DateTime.UtcNow,
                    CompletedAt: DateTime.UtcNow,
                    Tracks: tracks,
                    ReconstructionCommandTemplate: null,
                    LossyWarnings: []
                ),
            ]
        );

        string json = JsonConvert.SerializeObject(blueprint, Formatting.Indented);
        File.WriteAllText(Path.Combine(hostDir, MediaBlueprintWriter.FileName), json);
    }

    // -----------------------------------------------------------------------
    // Fixture helpers
    // -----------------------------------------------------------------------

    private static void WriteVariant(
        string hostDir,
        string dirName,
        int segmentBytes,
        string extension
    )
    {
        string variantDirectory = Path.Combine(hostDir, dirName);
        Directory.CreateDirectory(variantDirectory);

        byte[] segment = new byte[segmentBytes];
        File.WriteAllBytes(Path.Combine(variantDirectory, $"{dirName}_00000{extension}"), segment);

        string playlist =
            $"#EXTM3U\n#EXTINF:6.000000,\n{dirName}_00000{extension}\n#EXT-X-ENDLIST\n";
        File.WriteAllText(Path.Combine(variantDirectory, $"{dirName}.m3u8"), playlist);
    }

    private static void WriteInitMp4(string hostDir, string dirName) =>
        File.WriteAllBytes(Path.Combine(hostDir, dirName, "init.mp4"), new byte[512]);

    private static void WriteSubtitle(string hostDir, string language, string variant)
    {
        string subtitleDirectory = Path.Combine(hostDir, "subtitles", language);
        Directory.CreateDirectory(subtitleDirectory);
        File.WriteAllText(Path.Combine(subtitleDirectory, $"{variant}.ass"), "[Script Info]\n");
    }

    private void SetupProbe(
        string dirName,
        string codec,
        int width,
        int height,
        int bitDepth,
        string colorTransfer
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
                    PixelFormat: bitDepth >= 10 ? "yuv420p10le" : "yuv420p",
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

    private static int CountStreamInf(string master) =>
        master
            .Split('\n')
            .Count(line => line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal));

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

    // -----------------------------------------------------------------------
    // Private-method helpers via reflection — same convention as
    // FileManagerHashListTests.
    // -----------------------------------------------------------------------

    private FileManager BuildFileManager()
    {
        Mock<IFileRepository> repoMock = new();
        Mock<IStorageFactory> factoryMock = new();
        return new(
            repoMock.Object,
            factoryMock.Object,
            _mediaAnalyzer.Object,
            TestFilenameParser.Default
        );
    }

    private static List<IVideo> InvokeGetVideoHashList(
        FileManager manager,
        IStorage storage,
        string hostFolder
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "GetVideoHashList",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("GetVideoHashList not found");

        return (List<IVideo>)method.Invoke(manager, [storage, hostFolder])!;
    }

    private static async Task InvokeRebuildHlsMasterFromDiskAsync(
        FileManager manager,
        IStorage storage,
        string hostFolder,
        string fileName,
        List<IVideo> video
    )
    {
        MethodInfo method =
            typeof(FileManager).GetMethod(
                "RebuildHlsMasterFromDiskAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            ) ?? throw new InvalidOperationException("RebuildHlsMasterFromDiskAsync not found");

        await (Task)method.Invoke(manager, [storage, hostFolder, fileName, video])!;
    }
}
