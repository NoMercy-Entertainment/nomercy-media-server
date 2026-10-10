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

using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Output;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Output;

public class HlsOnDiskPlanReconstructorAudioTests : IDisposable
{
    private readonly string _outputDirectory;
    private readonly Mock<IMediaAnalyzer> _mediaAnalyzer = new();

    public HlsOnDiskPlanReconstructorAudioTests()
    {
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"nomercy-audio-reconstruct-{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, true);
    }

    [Fact]
    public async Task ReconstructAsync_OldStyleAudioDirNoCodecSuffix_ParsesLanguageCorrectly()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_jpn", "audio_jpn", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        AudioOutputPlan? audio = plan.AudioOutputs.FirstOrDefault();
        audio.Should().NotBeNull();
        audio!.Language.Should().Be("jpn");
        audio.EncoderName.Should().Be("aac");
    }

    [Fact]
    public async Task ReconstructAsync_NewStyleAudioDirWithCodecSuffix_ParsesLanguageAndCodec()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_jpn_aac", "audio_jpn_aac", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        AudioOutputPlan? audio = plan.AudioOutputs.FirstOrDefault();
        audio.Should().NotBeNull();
        audio!.Language.Should().Be("jpn");
        audio.EncoderName.Should().Be("aac");
    }

    [Fact]
    public async Task ReconstructAsync_MultipleLanguagesOldStyle_AllParsedCorrectly()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_eng", "audio_eng", segmentBytes: 60_000);
        WriteAudioVariant("audio_fra", "audio_fra", segmentBytes: 60_000);
        WriteAudioVariant("audio_jpn", "audio_jpn", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        plan.AudioOutputs.Should().HaveCount(3);

        string[] languages =
        [
            .. plan.AudioOutputs.Select(a => a.Language ?? string.Empty).OrderBy(l => l),
        ];
        languages.Should().Equal("eng", "fra", "jpn");

        foreach (AudioOutputPlan audio in plan.AudioOutputs)
        {
            audio.EncoderName.Should().Be("aac");
        }
    }

    [Fact]
    public async Task ReconstructAsync_MixedOldAndNewStyleAudio_AllParsedWithCorrectCodecs()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_eng_aac", "audio_eng_aac", segmentBytes: 60_000);
        WriteAudioVariant("audio_fra", "audio_fra", segmentBytes: 60_000);
        WriteAudioVariant("audio_jpn_opus", "audio_jpn_opus", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        AudioOutputPlan[] audioByLang =
        [
            .. plan.AudioOutputs.OrderBy(a => a.Language ?? string.Empty),
        ];

        audioByLang.Should().HaveCount(3);
        audioByLang[0].Language.Should().Be("eng");
        audioByLang[0].EncoderName.Should().Be("aac");
        audioByLang[1].Language.Should().Be("fra");
        audioByLang[1].EncoderName.Should().Be("aac");
        audioByLang[2].Language.Should().Be("jpn");
        audioByLang[2].EncoderName.Should().Be("opus");
    }

    [Fact]
    public async Task ReconstructAsync_AudioWithChannelSuffix_StillParsesLanguageCorrectly()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_eng_aac_2", "audio_eng_aac_2", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        AudioOutputPlan? audio = plan.AudioOutputs.FirstOrDefault();
        audio.Should().NotBeNull();
        audio!.Language.Should().Be("eng");
        audio.EncoderName.Should().Be("aac");
    }

    [Fact]
    public async Task ReconstructAsync_InvalidAudioDirName_SkipsInvalidName()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        Directory.CreateDirectory(Path.Combine(_outputDirectory, "audio_123"));
        WriteAudioVariant("audio_eng", "audio_eng", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        plan.AudioOutputs.Should().HaveCount(1);
        plan.AudioOutputs[0].Language.Should().Be("eng");
    }

    [Fact]
    public async Task ReconstructAsync_NoAudioDirs_ReturnsEmptyAudioOutputs()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        plan.AudioOutputs.Should().BeEmpty();
    }

    [Fact]
    public async Task ReconstructAsync_AudioMetricsNonZero_AudioIncludedInMaster()
    {
        IStorage storage = TestStorageFactory.CreateLocal();

        WriteAudioVariant("audio_eng", "audio_eng", segmentBytes: 60_000);
        WriteVideoVariant("video_1920x1080_SDR", "video_1920x1080_SDR", segmentBytes: 300_000);
        SetupVideoProbe("video_1920x1080_SDR", "hevc", 1920, 1080, 8, "bt709");

        HlsOnDiskPlanReconstructor reconstructor = new(_mediaAnalyzer.Object);
        OutputPlan plan = await reconstructor.ReconstructAsync(
            storage,
            _outputDirectory,
            CancellationToken.None
        );

        HlsOutputStrategy strategy = new(storage);
        await strategy.FinalizeAsync(_outputDirectory, plan, "Title", CancellationToken.None);

        string master = await File.ReadAllTextAsync(Path.Combine(_outputDirectory, "Title.m3u8"));

        master.Should().Contain("#EXT-X-MEDIA:TYPE=AUDIO");
        master.Should().Contain("LANGUAGE=\"eng\"");
        master.Should().Contain("GROUP-ID=\"audio_aac\"");
    }

    private void WriteAudioVariant(string subDirectory, string name, int segmentBytes)
    {
        string variantDirectory = Path.Combine(_outputDirectory, subDirectory);
        Directory.CreateDirectory(variantDirectory);

        byte[] segment = new byte[segmentBytes];
        File.WriteAllBytes(Path.Combine(variantDirectory, $"{name}_00000.m4s"), segment);

        string playlist = $"#EXTM3U\n#EXTINF:6.000000,\n{name}_00000.m4s\n#EXT-X-ENDLIST\n";
        File.WriteAllText(Path.Combine(variantDirectory, $"{name}.m3u8"), playlist);
    }

    private void WriteVideoVariant(string subDirectory, string name, int segmentBytes)
    {
        string variantDirectory = Path.Combine(_outputDirectory, subDirectory);
        Directory.CreateDirectory(variantDirectory);

        byte[] segment = new byte[segmentBytes];
        File.WriteAllBytes(Path.Combine(variantDirectory, $"{name}_00000.m4s"), segment);

        string playlist = $"#EXTM3U\n#EXTINF:6.000000,\n{name}_00000.m4s\n#EXT-X-ENDLIST\n";
        File.WriteAllText(Path.Combine(variantDirectory, $"{name}.m3u8"), playlist);

        File.WriteAllBytes(Path.Combine(variantDirectory, "init.mp4"), new byte[512]);
    }

    private void SetupVideoProbe(
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
                    ColorPrimaries: "bt709",
                    ColorTransfer: colorTransfer,
                    ColorSpace: "bt709",
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
}
