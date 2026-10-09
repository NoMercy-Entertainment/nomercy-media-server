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

using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using NoMercy.Database;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Startup;
using BuiltinPresets = NoMercy.Encoder.Profiles.BuiltinPresets;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using HardwarePreference = NoMercy.Encoder.Profiles.HardwarePreference;
using LoudnessMode = NoMercy.Encoder.Profiles.LoudnessMode;
using RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;
using SubtitlePolicy = NoMercy.Encoder.Profiles.SubtitlePolicy;

namespace NoMercy.Tests.Encoder.Integration;

[Trait("Category", "Integration")]
[Collection("RealEncode")]
public class RealEncodeTests : IAsyncLifetime
{
    private const string ForkRequiredSkipReason =
        "Requires nomercy-ffmpeg (spritevtt muxer). Set NOMERCY_FFMPEG_PATH or install via "
        + "AppData/Local/NoMercy_dev/binaries/ffmpeg.";

    private string _testDir = string.Empty;
    private string _inputFile = string.Empty;
    private ServiceProvider _serviceProvider = null!;
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private bool _forkSupportsSpritevtt;

    public async Task InitializeAsync()
    {
        // Resolve nomercy-ffmpeg before anything else. Real-encode tests use
        // the spritevtt muxer (fork-only) — pointing FfmpegPathOverride at the
        // system "ffmpeg" PATH would surface as "Unrecognized option
        // 'vtt_filename'" and report environment problems as code failures.
        _ffmpegPath = NoMercyFfmpegProbe.ResolveFfmpegPath();
        _ffprobePath = NoMercyFfmpegProbe.ResolveFfprobePath(_ffmpegPath);
        _forkSupportsSpritevtt =
            _ffmpegPath is not null && NoMercyFfmpegProbe.SupportsSpritevtt(_ffmpegPath);

        _testDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-encode-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_testDir);

        _inputFile = Path.Combine(_testDir, "test-input.mp4");

        // Generate a 3-second test clip — short to minimize encode time. Use
        // the resolved fork binary so the clip + the encode under test agree
        // on which ffmpeg they're driving.
        ProcessStartInfo psi = new()
        {
            FileName = _ffmpegPath ?? "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("testsrc2=size=320x180:rate=25:duration=3");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("sine=frequency=440:duration=3:sample_rate=44100");
        psi.ArgumentList.Add("-c:v");
        psi.ArgumentList.Add("libx264");
        psi.ArgumentList.Add("-preset");
        psi.ArgumentList.Add("ultrafast");
        psi.ArgumentList.Add("-crf");
        psi.ArgumentList.Add("51");
        psi.ArgumentList.Add("-c:a");
        psi.ArgumentList.Add("aac");
        psi.ArgumentList.Add("-b:a");
        psi.ArgumentList.Add("64k");
        psi.ArgumentList.Add(_inputFile);

        using Process process = Process.Start(psi)!;
        // Read both streams to prevent buffer deadlock
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stderr = await stderrTask;
        await stdoutTask;

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"FFmpeg test clip generation failed: {stderr}");
        }

        // Build DI — full encoder pipeline with software-only encoding for deterministic tests
        ServiceCollection services = new();
        services.AddLogging();
        // EF Core db-context factories needed by DbDriverFingerprintStore /
        // subscribers — in-memory provider keeps integration tests hermetic.
        string suffix = Guid.NewGuid().ToString("N")[..8];
        services.AddDbContextFactory<MediaContext>(o =>
            o.UseInMemoryDatabase($"real-media-{suffix}")
        );
        services.AddDbContextFactory<AppDbContext>(o =>
            o.UseInMemoryDatabase($"real-app-{suffix}")
        );
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = _ffmpegPath ?? "ffmpeg";
            opts.FfprobePathOverride = _ffprobePath ?? "ffprobe";
        });

        // Force software encoding — override hardware detector so tests don't depend on GPU
        services.AddSingleton<IHardwareDetector, NullHardwareDetector>();

        // IHostApplicationLifetime is normally added by HostBuilder; stub it
        // here so resolving HardwareInitializationService (which depends on
        // BenchmarkJobTracker) works without a full host.
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();

        _serviceProvider = services.BuildServiceProvider();

        // Probe FFmpeg capabilities
        HardwareInitializationService hwInit =
            _serviceProvider.GetRequiredService<HardwareInitializationService>();
        await hwInit.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();

        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, recursive: true);
        }
        catch
        {
            // Best-effort cleanup
        }

        return Task.CompletedTask;
    }

    [SkippableFact]
    public async Task EncodeAsync_HlsProfile_ProducesPlaylistAndSegments()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));

        string outputDir = Path.Combine(_testDir, "output");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-180p",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 320,
                Height: 180,
                RateControl: RateControlMode.Crf,
                Crf: 40,
                BitrateKbps: 200,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                // Use MP3 — libmp3lame is in standard FFmpeg builds AND is a valid HlsTs
                // codec. libfdk_aac (AudioCodecType.Aac) is bundle-only and Opus is
                // not in the HlsTs compatibility matrix, so neither works for these
                // integration tests when running against system FFmpeg.
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 64,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: _inputFile,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );
        result.OutputPath.Should().NotBeNullOrWhiteSpace();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);
        result.Metrics.Should().NotBeNull();
        result.Metrics!.EncoderUsed.Should().NotBeNullOrWhiteSpace();

        // Verify HLS: at least one playlist and at least one segment
        string[] playlists = Directory.GetFiles(outputDir, "*.m3u8", SearchOption.AllDirectories);
        string[] segments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        playlists.Should().NotBeEmpty("HLS output should contain at least one .m3u8 playlist");
        segments.Should().NotBeEmpty("HLS output should contain at least one .ts segment");

        // Verify progress observer received at least one callback (stage-completed at end)
        (observer.StagesStarted.Count + observer.ProgressCallCount)
            .Should()
            .BeGreaterThan(0, "should receive at least one progress callback");
    }

    [SkippableFact]
    public async Task EncodeAsync_ScalingProfile_ProducesDownscaledOutput()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));

        string outputDir = Path.Combine(_testDir, "output-scale");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-90p-scale",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 160,
                Height: null,
                RateControl: RateControlMode.Crf,
                Crf: 40,
                BitrateKbps: 100,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 64,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: _inputFile,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        // Find the video output directory (template resolves dimensions + colorrange)
        string[] allDirs = Directory.GetDirectories(outputDir);
        string[] allFiles = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
        string[] videoDirs = Directory.GetDirectories(outputDir, "video_*");
        videoDirs
            .Should()
            .HaveCount(
                1,
                $"should have exactly one video output directory. All dirs: [{string.Join(", ", allDirs.Select(Path.GetFileName))}]. All files: [{string.Join(", ", allFiles.Select(f => Path.GetRelativePath(outputDir, f)))}]"
            );

        string[] playlists = Directory.GetFiles(
            videoDirs[0],
            "*.m3u8",
            SearchOption.TopDirectoryOnly
        );
        string[] segments = Directory.GetFiles(videoDirs[0], "*.ts", SearchOption.TopDirectoryOnly);

        playlists.Should().NotBeEmpty("video dir should contain a .m3u8 playlist");
        segments.Should().NotBeEmpty("video dir should contain .ts segments");
    }

    [SkippableFact]
    public async Task EncodeAsync_MultiOutputProfile_ProducesMultipleVariants()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));

        string outputDir = Path.Combine(_testDir, "output-multi");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-multi",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 320,
                Height: 180,
                RateControl: RateControlMode.Crf,
                Crf: 40,
                BitrateKbps: 200,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 64,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: [],
            Ladder: new()
            {
                Mode = NoMercy.Encoder.Profiles.LadderMode.Manual,
                // Validator requires manual rungs sorted ascending by bitrate
                // (100 kbps before 200 kbps) — keeps master-playlist BANDWIDTH
                // ordering deterministic.
                Rungs =
                [
                    new(160, 90, VideoCodecType.H264, 100, 200, 400, 25.0, "ultrafast"),
                    new(320, 180, VideoCodecType.H264, 200, 400, 800, 25.0, "ultrafast"),
                ],
            }
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: _inputFile,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        // Should have 2 video variant directories
        string[] videoDirs = Directory.GetDirectories(outputDir, "video_*");
        videoDirs.Should().HaveCount(2, "should have two video output directories");

        foreach (string dir in videoDirs)
        {
            Directory
                .GetFiles(dir, "*.m3u8", SearchOption.TopDirectoryOnly)
                .Should()
                .NotBeEmpty($"{Path.GetFileName(dir)} should contain a .m3u8 playlist");
            Directory
                .GetFiles(dir, "*.ts", SearchOption.TopDirectoryOnly)
                .Should()
                .NotBeEmpty($"{Path.GetFileName(dir)} should contain .ts segments");
        }
    }

    [SkippableFact]
    public async Task EncodeAsync_ScalingProfile_ProducesCorrectResolution()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(3));

        string outputDir = Path.Combine(_testDir, "output-scale-160x90");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-160x90-scaling",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 160,
                Height: 90,
                RateControl: RateControlMode.Crf,
                Crf: 40,
                BitrateKbps: 100,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 64,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: _inputFile,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        // Verify a video_160x90 (or video_160x90_sdtv) directory exists with playlist + segments
        string[] videoDirs = Directory.GetDirectories(outputDir, "video_160x90*");
        videoDirs
            .Should()
            .HaveCount(
                1,
                $"should have exactly one video output directory for 160x90. Found: [{string.Join(", ", Directory.GetDirectories(outputDir).Select(Path.GetFileName))}]"
            );

        string videoDir = videoDirs[0];
        Directory
            .GetFiles(videoDir, "*.m3u8", SearchOption.TopDirectoryOnly)
            .Should()
            .NotBeEmpty("video_160x90 dir should contain a .m3u8 playlist");
        Directory
            .GetFiles(videoDir, "*.ts", SearchOption.TopDirectoryOnly)
            .Should()
            .NotBeEmpty("video_160x90 dir should contain .ts segments");
    }

    [SkippableFact]
    public async Task EncodeAsync_HlsTwoPassProfile_ProducesValidPlaylist()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        // Two-pass encodes are slower — give them a longer timeout.
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string outputDir = Path.Combine(_testDir, "output-twopass");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-twopass-180p",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 320,
                Height: 180,
                RateControl: RateControlMode.Crf,
                Crf: 40,
                BitrateKbps: 500,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 64,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: [],
            EncodeMode: EncodeMode.TwoPass
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: _inputFile,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Two-pass encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );
        result.OutputPath.Should().NotBeNullOrWhiteSpace();
        result.Duration.Should().BeGreaterThan(TimeSpan.Zero);

        // Verify HLS output: at least one playlist and at least one segment
        string[] playlists = Directory.GetFiles(outputDir, "*.m3u8", SearchOption.AllDirectories);
        string[] segments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        playlists
            .Should()
            .NotBeEmpty("two-pass HLS output should contain at least one .m3u8 playlist");
        segments.Should().NotBeEmpty("two-pass HLS output should contain at least one .ts segment");

        // Verify all referenced segments actually exist on disk
        foreach (string playlist in playlists)
        {
            string playlistDir = Path.GetDirectoryName(playlist)!;
            string[] lines = await File.ReadAllLinesAsync(playlist, cts.Token);
            IEnumerable<string> segmentRefs = lines.Where(l =>
                !l.StartsWith('#') && (l.EndsWith(".ts") || l.EndsWith(".mp4"))
            );

            foreach (string segRef in segmentRefs)
            {
                string segPath = Path.IsPathRooted(segRef)
                    ? segRef
                    : Path.Combine(playlistDir, segRef);
                File.Exists(segPath)
                    .Should()
                    .BeTrue(
                        $"segment '{segRef}' referenced in '{Path.GetFileName(playlist)}' should exist on disk"
                    );
            }
        }

        // Verify pass-1 stats files are cleaned up after successful two-pass encode
        string statsDir = Path.Combine(outputDir, ".2pass");
        bool anyStatsRemaining =
            Directory.Exists(statsDir)
            && Directory.GetFiles(statsDir, "*.log", SearchOption.AllDirectories).Length > 0;
        anyStatsRemaining
            .Should()
            .BeFalse(
                "pass-1 .log stats files should be deleted after a successful two-pass encode"
            );
    }

    [Fact]
    public void SeedProfiles_DeserializeToValidEncodingProfiles()
    {
        IReadOnlyList<EncodingProfile> profiles = BuiltinPresets.All();

        profiles.Should().NotBeEmpty("BuiltinPresets.All() must return at least one profile");

        foreach (EncodingProfile profile in profiles)
        {
            // Roundtrip: serialize → deserialize
            string json = JsonConvert.SerializeObject(profile);
            EncodingProfile? deserialized = JsonConvert.DeserializeObject<EncodingProfile>(json);

            deserialized
                .Should()
                .NotBeNull($"profile '{profile.Name}' should deserialize without error");
            deserialized!
                .Name.Should()
                .NotBeNullOrWhiteSpace(
                    $"profile '{profile.Name}' should have a non-empty Name after roundtrip"
                );

            bool hasAudioOutput = deserialized.Audio.Length > 0;
            bool hasVideoOutput = deserialized.Video != null || deserialized.Ladder != null;
            (hasAudioOutput || hasVideoOutput)
                .Should()
                .BeTrue($"profile '{profile.Name}' should have at least one audio or video output");
        }
    }

    [Fact]
    public void V3EncodingProfile_SerializationRoundtrip_PreservesAllFields()
    {
        EncodingProfile original = new(
            Id: Ulid.NewUlid(),
            Name: "roundtrip-test-profile",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: RateControlMode.Crf,
                Crf: 23,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "medium",
                CodecProfile: CodecProfile.High,
                Level: "4.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: "video/{label}",
                PlaylistNameTemplate: "video/{label}/playlist"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 128,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["eng", "und"],
                    DefaultLanguage: null,
                    Loudness: null,
                    Downmix: null,
                    SegmentNameTemplate: "audio/{lang}-{codec}",
                    PlaylistNameTemplate: "audio/{lang}-{codec}/playlist"
                ),
            ],
            Subtitles:
            [
                new(
                    Policy: SubtitlePolicy.Extract,
                    Codec: SubtitleCodecType.WebVtt,
                    AllowedLanguages: ["eng"],
                    IncludeForced: true,
                    OcrLanguage: null,
                    PlaylistNameTemplate: "subs/{lang}"
                ),
            ],
            Thumbnails: new(Width: 320, IntervalSeconds: 10)
        );

        string json = JsonConvert.SerializeObject(original);
        EncodingProfile? deserialized = JsonConvert.DeserializeObject<EncodingProfile>(json);

        deserialized.Should().NotBeNull();
        deserialized!.Name.Should().Be(original.Name);
        deserialized.Container.Should().Be(original.Container);
        deserialized.SchemaVersion.Should().Be(original.SchemaVersion);

        deserialized.Video.Should().NotBeNull();
        deserialized.Video!.Width.Should().Be(1920);
        deserialized.Video.Height.Should().Be(1080);
        deserialized.Video.Codec.Should().Be(VideoCodecType.H264);

        deserialized.Audio.Should().HaveCount(original.Audio.Length);
        deserialized.Audio[0].Codec.Should().Be(AudioCodecType.Mp3);
        deserialized.Audio[0].BitrateKbps.Should().Be(128);
        deserialized.Audio[0].AllowedLanguages.Should().BeEquivalentTo("eng", "und");

        deserialized.Subtitles.Should().HaveCount(original.Subtitles.Length);
        deserialized.Subtitles[0].Codec.Should().Be(SubtitleCodecType.WebVtt);
        deserialized.Subtitles[0].Policy.Should().Be(SubtitlePolicy.Extract);

        deserialized.Thumbnails.Should().NotBeNull();
        deserialized.Thumbnails!.Width.Should().Be(320);
        deserialized.Thumbnails.IntervalSeconds.Should().Be(10);
    }

    // -------------------------------------------------------------------------
    // Regression matrix: four content classes from the encoder plan.
    // Each resolves a fixture file; if absent, the test skips cleanly.
    // Fixture root: NOMERCY_TEST_FIXTURES_PATH env var, or
    //   %LOCALAPPDATA%/NoMercy_dev/test-fixtures (Windows) /
    //   ~/.local/share/NoMercy_dev/test-fixtures (Linux).
    // -------------------------------------------------------------------------

    // Resolves a fixture by name. Prefers a real media file on disk
    // (NOMERCY_TEST_FIXTURES_PATH or the NoMercy_dev/test-fixtures dirs); when
    // none is present, synthesizes a deterministic stand-in with the resolved
    // fork ffmpeg so the regression test runs everywhere instead of skipping.
    // Generic file names — these are lavfi-generated, not real titles.
    private string ResolveOrGenerateFixture(string relativeFileName)
    {
        string? real = ResolveRealFixture(relativeFileName);
        if (real is not null)
            return real;

        return GenerateSyntheticFixture(relativeFileName);
    }

    private static string? ResolveRealFixture(string relativeFileName)
    {
        string? envRoot = Environment.GetEnvironmentVariable("NOMERCY_TEST_FIXTURES_PATH");
        if (!string.IsNullOrWhiteSpace(envRoot))
        {
            string candidate = Path.Combine(envRoot, relativeFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        string? localAppData = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData
        );
        string? home = Environment.GetEnvironmentVariable("HOME");

        foreach (
            string root in new[]
            {
                !string.IsNullOrWhiteSpace(localAppData)
                    ? Path.Combine(localAppData, "NoMercy_dev", "test-fixtures")
                    : null,
                !string.IsNullOrWhiteSpace(localAppData)
                    ? Path.Combine(localAppData, "NoMercy", "test-fixtures")
                    : null,
                !string.IsNullOrWhiteSpace(home)
                    ? Path.Combine(home, ".local", "share", "NoMercy_dev", "test-fixtures")
                    : null,
            }.OfType<string>()
        )
        {
            string candidate = Path.Combine(root, relativeFileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    // lavfi recipes per fixture name. Each is a short, deterministic clip that
    // exercises the content class the test cares about (SD, 1080p, HDR10 PQ,
    // audio-only) without depending on real media.
    private string GenerateSyntheticFixture(string relativeFileName)
    {
        string outPath = Path.Combine(_testDir, relativeFileName);
        if (File.Exists(outPath))
            return outPath;

        List<string> args = relativeFileName switch
        {
            "video-sd480p.mkv" =>
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=854x480:rate=25:duration=5",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:duration=5",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-crf",
                "30",
                "-c:a",
                "aac",
                "-b:a",
                "96k",
            ],
            "video-1080p.mkv" =>
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=1920x1080:rate=25:duration=5",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:duration=5",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-crf",
                "30",
                "-c:a",
                "aac",
                "-b:a",
                "96k",
            ],
            "video-hdr10.mkv" =>
            [
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=1920x1080:rate=25:duration=5",
                "-vf",
                "format=yuv420p10le,setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
                "-c:v",
                "libx265",
                "-preset",
                "ultrafast",
                "-x265-params",
                "hdr10=1:colorprim=bt2020:transfer=smpte2084:colormatrix=bt2020nc",
                "-color_primaries",
                "bt2020",
                "-color_trc",
                "smpte2084",
                "-colorspace",
                "bt2020nc",
            ],
            "audio-only.flac" =>
            [
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:duration=5",
                "-c:a",
                "flac",
            ],
            _ => throw new ArgumentOutOfRangeException(
                nameof(relativeFileName),
                $"No synthetic recipe for fixture '{relativeFileName}'"
            ),
        };

        ProcessStartInfo psi = new()
        {
            FileName = _ffmpegPath ?? "ffmpeg",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("-y");
        foreach (string arg in args)
            psi.ArgumentList.Add(arg);
        psi.ArgumentList.Add(outPath);

        using Process process = Process.Start(psi)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"Synthetic fixture generation failed for '{relativeFileName}': {stderr}"
            );

        return outPath;
    }

    [SkippableFact]
    public async Task EncodeAsync_Sd480p_DarkwingDuck_ProducesPlaylistAndSegments()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);

        string fixture = ResolveOrGenerateFixture("video-sd480p.mkv");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(10));

        string outputDir = Path.Combine(_testDir, "output-sd480p");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-sd480p",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 854,
                Height: 480,
                RateControl: RateControlMode.Crf,
                Crf: 28,
                BitrateKbps: 800,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.Baseline,
                Level: "3.1",
                Tune: null,
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 128,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und", "eng"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: fixture,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"SD 480p encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        string[] playlists = Directory.GetFiles(outputDir, "*.m3u8", SearchOption.AllDirectories);
        string[] segments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        playlists
            .Should()
            .NotBeEmpty("SD 480p HLS output should contain at least one .m3u8 playlist");
        segments.Should().NotBeEmpty("SD 480p HLS output should contain at least one .ts segment");

        // Verify the output directory contains a video folder for the expected resolution
        string[] videoDirs = Directory.GetDirectories(outputDir, "video_854x480*");
        videoDirs
            .Should()
            .HaveCount(
                1,
                $"should have exactly one video_854x480 directory. Found: [{string.Join(", ", Directory.GetDirectories(outputDir).Select(Path.GetFileName))}]"
            );
    }

    [SkippableFact]
    public async Task EncodeAsync_1080p_Anime_NoGameNoLife_ProducesPlaylistAndSegments()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);

        string fixture = ResolveOrGenerateFixture("video-1080p.mkv");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(15));

        string outputDir = Path.Combine(_testDir, "output-1080p-anime");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-1080p-anime",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: RateControlMode.Crf,
                Crf: 20,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.High,
                Level: "4.0",
                Tune: "animation",
                BitDepth: 8,
                PixelFormat: null,
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: false,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 192,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["jpn", "und"],
                    DefaultLanguage: "jpn",
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: fixture,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"1080p anime encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        string[] playlists = Directory.GetFiles(outputDir, "*.m3u8", SearchOption.AllDirectories);
        string[] segments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        playlists
            .Should()
            .NotBeEmpty("1080p anime HLS output should contain at least one .m3u8 playlist");
        segments
            .Should()
            .NotBeEmpty("1080p anime HLS output should contain at least one .ts segment");

        string[] videoDirs = Directory.GetDirectories(outputDir, "video_1920x1080*");
        videoDirs
            .Should()
            .HaveCount(
                1,
                $"should have exactly one video_1920x1080 directory. Found: [{string.Join(", ", Directory.GetDirectories(outputDir).Select(Path.GetFileName))}]"
            );
    }

    [SkippableFact]
    public async Task EncodeAsync_HdrContent_ProducesTonemappedSdrPlaylist()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);

        string fixture = ResolveOrGenerateFixture("video-hdr10.mkv");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(15));

        string outputDir = Path.Combine(_testDir, "output-hdr");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-hdr-tonemap",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: RateControlMode.Crf,
                Crf: 22,
                BitrateKbps: 4000,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "ultrafast",
                CodecProfile: CodecProfile.High,
                Level: "4.0",
                Tune: null,
                BitDepth: 8,
                PixelFormat: "yuv420p",
                KeyframeIntervalSeconds: 2,
                ConvertHdrToSdr: true,
                SegmentNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:",
                PlaylistNameTemplate: ":type:_:framesize:_:colorrange:/:type:_:framesize:_:colorrange:"
            ),
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 192,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und", "eng"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: fixture,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"HDR tonemapping encode failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        string[] playlists = Directory.GetFiles(outputDir, "*.m3u8", SearchOption.AllDirectories);
        string[] segments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        playlists.Should().NotBeEmpty("HDR HLS output should contain at least one .m3u8 playlist");
        segments.Should().NotBeEmpty("HDR HLS output should contain at least one .ts segment");

        // Master playlist must declare SDR range (tonemapping was applied)
        string? masterPlaylist = playlists.FirstOrDefault(p =>
            Path.GetFileName(p).Equals("master.m3u8", StringComparison.OrdinalIgnoreCase)
        );
        if (masterPlaylist is not null)
        {
            string masterContent = await File.ReadAllTextAsync(masterPlaylist, cts.Token);
            masterContent
                .Should()
                .NotContain(
                    "VIDEO-RANGE=PQ",
                    "tonemapping to SDR should remove HDR PQ video range from master playlist"
                );
        }
    }

    [SkippableFact]
    public async Task EncodeAsync_AudioOnly_ProducesAudioHlsPlaylist()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);

        string fixture = ResolveOrGenerateFixture("audio-only.flac");

        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        string outputDir = Path.Combine(_testDir, "output-audio-only");
        Directory.CreateDirectory(outputDir);

        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-audio-only",
            Container: Container.HlsTs,
            Video: null,
            Audio:
            [
                new(
                    Policy: StreamPolicy.Transcode,
                    Codec: AudioCodecType.Mp3,
                    BitrateKbps: 320,
                    Channels: 2,
                    SampleRateHz: 48000,
                    AllowedLanguages: ["und", "eng"],
                    DefaultLanguage: null,
                    Loudness: new(LoudnessMode.None),
                    Downmix: null,
                    SegmentNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:",
                    PlaylistNameTemplate: ":type:_:language:_:codec:/:type:_:language:_:codec:"
                ),
            ],
            Subtitles: []
        )
        {
            HardwarePreference = HardwarePreference.ForceSoftware,
            HlsDerivatives = new()
            {
                GenerateSpriteVtt = false,
                GenerateThumbnailTrack = false,
                GenerateMetadataJson = false,
                GenerateChapters = false,
                GenerateFontsJson = false,
            },
        };

        EncodingRequest request = new(
            InputPath: fixture,
            OutputDirectory: outputDir,
            Profile: profile
        );

        TestProgressObserver observer = new();
        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();

        EncodingResult result = await encoder.EncodeAsync(request, observer, cts.Token);

        result
            .Success.Should()
            .BeTrue(
                $"Audio-only encoding failed: {result.Error?.Message} | stderr: {result.Error?.FfmpegStderr}"
            );

        // Audio-only: must produce audio segment directories and playlists; must NOT produce video dirs
        string[] allPlaylists = Directory.GetFiles(
            outputDir,
            "*.m3u8",
            SearchOption.AllDirectories
        );
        string[] audioSegments = Directory.GetFiles(outputDir, "*.ts", SearchOption.AllDirectories);

        allPlaylists
            .Should()
            .NotBeEmpty("audio-only output should contain at least one .m3u8 playlist");
        audioSegments
            .Should()
            .NotBeEmpty("audio-only output should contain at least one .ts segment");

        string[] videoDirs = Directory.GetDirectories(outputDir, "video_*");
        videoDirs.Should().BeEmpty("audio-only encode should not produce any video_ directories");

        string[] audioDirs = Directory.GetDirectories(outputDir, "audio_*");
        audioDirs
            .Should()
            .NotBeEmpty("audio-only encode should produce at least one audio_ directory");
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }

    private class TestProgressObserver : IProgressObserver
    {
        public List<string> StagesStarted { get; } = [];
        public int ProgressCallCount { get; private set; }

        public void OnStageStarted(string stageName) => StagesStarted.Add(stageName);

        public void OnProgress(EncodingProgress progress) => ProgressCallCount++;

        public void OnStageCompleted(string stageName, TimeSpan duration) { }

        public void OnCompleted() { }

        public void OnPlanResolved(
            List<string> videoStreams,
            List<string> audioStreams,
            List<string> subtitleStreams,
            bool hasGpu,
            bool isHdr
        ) { }

        public void OnError(EncodingError error) { }
    }
}
