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
using System.Globalization;
using FluentAssertions.Execution;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NoMercy.Database;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Startup;
using CodecProfile = NoMercy.Encoder.Profiles.CodecProfile;
using Container = NoMercy.Encoder.Profiles.Container;
using EncodingProfile = NoMercy.Encoder.Profiles.EncodingProfile;
using HardwarePreference = NoMercy.Encoder.Profiles.HardwarePreference;
using LoudnessMode = NoMercy.Encoder.Profiles.LoudnessMode;
using RateControlMode = NoMercy.Encoder.Profiles.RateControlMode;
using StreamPolicy = NoMercy.Encoder.Profiles.StreamPolicy;

namespace NoMercy.Tests.Encoder.Integration;

/// <summary>
/// A real ffmpeg encode is stopped part way and run again on the same folder. The
/// bundle it ends with must equal the bundle of an encode that was never stopped.
/// </summary>
[Trait("Category", "Integration")]
[Collection("RealEncode")]
public class InterruptedEncodeResumeTests : IAsyncLifetime
{
    private const string ForkRequiredSkipReason =
        "Requires nomercy-ffmpeg (spritevtt muxer). Set NOMERCY_FFMPEG_PATH or install via "
        + "AppData/Local/NoMercy_dev/binaries/ffmpeg.";

    // ffmpeg's video lags its audio by many seconds at this preset, so three 2 s
    // segments per variant is as far as the stop can wait before the encode ends.
    private const int SegmentsBeforeStop = 3;

    // One AAC frame at 48 kHz (21.33 ms) is the most the joined audio may gap or overlap.
    private const double AudioSeamLimitMs = 1024 * 1000.0 / 48000 + 0.01;

    private string _testDir = string.Empty;
    private string _inputFile = string.Empty;
    private ServiceProvider _serviceProvider = null!;
    private string? _ffmpegPath;
    private string? _ffprobePath;
    private bool _forkSupportsSpritevtt;

    public async Task InitializeAsync()
    {
        _ffmpegPath = NoMercyFfmpegProbe.ResolveFfmpegPath();
        _ffprobePath = NoMercyFfmpegProbe.ResolveFfprobePath(_ffmpegPath);
        _forkSupportsSpritevtt =
            _ffmpegPath is not null && NoMercyFfmpegProbe.SupportsSpritevtt(_ffmpegPath);

        _testDir = Path.Combine(
            Path.GetTempPath(),
            "nomercy-resume-test-" + Guid.NewGuid().ToString("N")[..8]
        );
        Directory.CreateDirectory(_testDir);

        if (!_forkSupportsSpritevtt)
            return;

        // yuv420p is explicit: the lavfi default is 4:4:4, which the High profile refuses.
        _inputFile = Path.Combine(_testDir, "test-input.mkv");
        await RunToolAsync(
            _ffmpegPath!,
            [
                "-y",
                "-f",
                "lavfi",
                "-i",
                "testsrc2=size=1920x1080:rate=25:duration=20",
                "-f",
                "lavfi",
                "-i",
                "sine=frequency=440:duration=20:sample_rate=48000",
                "-c:v",
                "libx264",
                "-preset",
                "ultrafast",
                "-crf",
                "40",
                "-pix_fmt",
                "yuv420p",
                "-c:a",
                "aac",
                "-b:a",
                "96k",
                _inputFile,
            ]
        );

        ServiceCollection services = new();
        services.AddLogging();
        string suffix = Guid.NewGuid().ToString("N")[..8];
        services.AddDbContextFactory<MediaContext>(o =>
            o.UseInMemoryDatabase($"resume-media-{suffix}")
        );
        services.AddDbContextFactory<AppDbContext>(o =>
            o.UseInMemoryDatabase($"resume-app-{suffix}")
        );
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = _ffmpegPath!;
            opts.FfprobePathOverride = _ffprobePath ?? "ffprobe";
        });
        services.AddSingleton<IHardwareDetector, NullHardwareDetector>();
        services.AddSingleton<IHostApplicationLifetime, TestHostLifetime>();

        _serviceProvider = services.BuildServiceProvider();

        HardwareInitializationService hwInit =
            _serviceProvider.GetRequiredService<HardwareInitializationService>();
        await hwInit.StartAsync(CancellationToken.None);
    }

    public Task DisposeAsync()
    {
        _serviceProvider?.Dispose();

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
    public async Task InterruptedEncode_ResumedOnTheSameFolder_EqualsAnUninterruptedEncode()
    {
        Skip.IfNot(_forkSupportsSpritevtt, ForkRequiredSkipReason);
        using CancellationTokenSource cts = new(TimeSpan.FromMinutes(5));

        // Same leaf name: the master playlist is named after it.
        string uninterruptedDir = Path.Combine(_testDir, "uninterrupted", "bundle");
        string resumedDir = Path.Combine(_testDir, "resumed", "bundle");
        Directory.CreateDirectory(uninterruptedDir);
        Directory.CreateDirectory(resumedDir);

        EncodingResult whole = await EncodeAsync(uninterruptedDir, null, cts.Token);
        whole.Success.Should().BeTrue($"the uninterrupted encode failed: {whole.Error?.Message}");

        StopAtOutputObserver stopper = new(resumedDir, SegmentsBeforeStop);
        EncodingResult interrupted = await EncodeAsync(resumedDir, stopper, cts.Token);
        await stopper.Watcher;
        stopper.Stopped.Should().BeTrue("the test must stop ffmpeg while it is still encoding");
        interrupted.Success.Should().BeFalse("a stopped ffmpeg ends the encode with an error");

        // The newest segment of each variant may be cut short by the stop and is written again.
        Dictionary<string, DateTime> allSegments = SegmentWriteTimes(resumedDir);
        int complete = Directory
            .GetDirectories(resumedDir)
            .Min(d => Directory.GetFiles(d, "*.ts").Length - 1);
        complete
            .Should()
            .BeGreaterThan(1, "the stopped run left finished segments to continue from");
        Dictionary<string, DateTime> keptSegments = allSegments
            .GroupBy(s => Path.GetDirectoryName(s.Key))
            .SelectMany(g => g.OrderBy(s => s.Key, StringComparer.Ordinal).Take(complete))
            .ToDictionary(s => s.Key, s => s.Value);

        EncodingResult resumed = await EncodeAsync(resumedDir, null, cts.Token);
        resumed.Success.Should().BeTrue($"the resumed encode failed: {resumed.Error?.Message}");

        // The first segments were kept, not written again.
        Dictionary<string, DateTime> after = SegmentWriteTimes(resumedDir);
        foreach ((string segment, DateTime written) in keptSegments)
            after[segment].Should().Be(written, $"{segment} must not be encoded again");

        RelativeFiles(resumedDir)
            .Should()
            .BeEquivalentTo(RelativeFiles(uninterruptedDir), "both bundles hold the same files");

        using AssertionScope scope = new();

        // Playlists: the video variant is byte-equal; the audio cut can sit elsewhere.
        foreach (string playlist in RelativeFiles(uninterruptedDir).Where(IsVideoPlaylist))
        {
            File.ReadAllText(Path.Combine(resumedDir, playlist))
                .Should()
                .Be(File.ReadAllText(Path.Combine(uninterruptedDir, playlist)), playlist);
        }

        foreach (string sprite in RelativeFiles(uninterruptedDir).Where(IsSpriteFile))
        {
            File.ReadAllBytes(Path.Combine(resumedDir, sprite))
                .Should()
                .Equal(File.ReadAllBytes(Path.Combine(uninterruptedDir, sprite)), sprite);
        }

        foreach (
            string playlist in Directory.GetFiles(resumedDir, "*.m3u8", SearchOption.AllDirectories)
        )
        {
            string playlistDir = Path.GetDirectoryName(playlist)!;
            foreach (string line in File.ReadAllLines(playlist).Where(l => !l.StartsWith('#')))
            {
                if (line.Length == 0)
                    continue;

                File.Exists(Path.Combine(playlistDir, line))
                    .Should()
                    .BeTrue($"{line} in {Path.GetFileName(playlist)} must exist");
            }
        }

        Directory
            .GetFiles(resumedDir, ".resume.*", SearchOption.AllDirectories)
            .Should()
            .BeEmpty("a finished encode leaves no resume marker behind");

        double seamMs = await WorstAudioGapMsAsync(resumedDir);
        double wholeMs = await WorstAudioGapMsAsync(uninterruptedDir);
        Console.WriteLine(
            $"audio gap, worst absolute: resumed {seamMs:F1} ms, uninterrupted {wholeMs:F1} ms"
        );
        seamMs.Should().BeLessThanOrEqualTo(AudioSeamLimitMs);
    }

    private async Task<EncodingResult> EncodeAsync(
        string outputDir,
        IProgressObserver? observer,
        CancellationToken ct
    )
    {
        EncodingProfile profile = new(
            Id: Ulid.NewUlid(),
            Name: "test-hls-resume",
            Container: Container.HlsTs,
            Video: new(
                Policy: StreamPolicy.Transcode,
                Codec: VideoCodecType.H264,
                Width: 1920,
                Height: 1080,
                RateControl: RateControlMode.Crf,
                Crf: 28,
                BitrateKbps: 500,
                MaxBitrateKbps: null,
                BufferSizeKbps: null,
                Preset: "veryslow",
                CodecProfile: CodecProfile.High,
                Level: "4.0",
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
                    Codec: AudioCodecType.Aac,
                    BitrateKbps: 96,
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
                GenerateSpriteVtt = true,
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

        IEncoder encoder = _serviceProvider.GetRequiredService<IEncoder>();
        return await encoder.EncodeAsync(request, observer, ct);
    }

    private static Dictionary<string, DateTime> SegmentWriteTimes(string dir) =>
        Directory
            .GetFiles(dir, "*.ts", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetRelativePath(dir, f), f => File.GetLastWriteTimeUtc(f));

    private static List<string> RelativeFiles(string dir) =>
        Directory
            .GetFiles(dir, "*", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(dir, f))
            .ToList();

    private static bool IsVideoPlaylist(string relative) =>
        relative.EndsWith(".m3u8", StringComparison.Ordinal)
        && Path.GetFileName(Path.GetDirectoryName(relative) ?? string.Empty)
            .StartsWith("video_", StringComparison.Ordinal);

    private static bool IsSpriteFile(string relative) =>
        relative.EndsWith(".webp", StringComparison.Ordinal)
        || relative.EndsWith(".vtt", StringComparison.Ordinal);

    // Joins the audio segments in order and measures the gap between adjacent packets.
    private async Task<double> WorstAudioGapMsAsync(string dir)
    {
        string audioDir = Directory.GetDirectories(dir, "audio_*").Single();
        string joined = Path.Combine(_testDir, Guid.NewGuid().ToString("N")[..8] + ".ts");
        await using (FileStream target = File.Create(joined))
        {
            foreach (
                string segment in Directory.GetFiles(audioDir, "*.ts").Order(StringComparer.Ordinal)
            )
            {
                await using FileStream source = File.OpenRead(segment);
                await source.CopyToAsync(target);
            }
        }

        string output = await RunToolAsync(
            _ffprobePath ?? "ffprobe",
            [
                "-v",
                "error",
                "-select_streams",
                "a:0",
                "-show_entries",
                "packet=pts_time,duration_time",
                "-of",
                "csv=p=0",
                joined,
            ]
        );

        List<(double Pts, double Duration)> packets = [];
        foreach (string line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = line.Trim().Split(',');
            if (parts.Length < 2)
                continue;

            packets.Add(
                (
                    double.Parse(parts[0], CultureInfo.InvariantCulture),
                    double.Parse(parts[1], CultureInfo.InvariantCulture)
                )
            );
        }

        packets.Count.Should().BeGreaterThan(10, "the joined audio must hold its packets");

        double worst = 0;
        for (int i = 1; i < packets.Count; i++)
        {
            double gapMs = (packets[i].Pts - (packets[i - 1].Pts + packets[i - 1].Duration)) * 1000;
            worst = Math.Max(worst, Math.Abs(gapMs));
        }

        return worst;
    }

    private static async Task<string> RunToolAsync(string tool, string[] arguments)
    {
        ProcessStartInfo psi = new()
        {
            FileName = tool,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string argument in arguments)
            psi.ArgumentList.Add(argument);

        using Process process = Process.Start(psi)!;
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        string stderr = await stderrTask;
        string stdout = await stdoutTask;

        if (process.ExitCode != 0)
            throw new InvalidOperationException($"{Path.GetFileName(tool)} failed: {stderr}");

        return stdout;
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopping { get; } = CancellationToken.None;
        public CancellationToken ApplicationStopped { get; } = CancellationToken.None;

        public void StopApplication() { }
    }

    // Ends the ffmpeg process it is told about once every variant folder holds enough
    // segments. Progress arrives about twice a second, too coarse to catch the moment.
    private sealed class StopAtOutputObserver(string outputDir, int segmentsWanted)
        : IProgressObserver
    {
        private int _watching;

        public bool Stopped { get; private set; }
        public Task Watcher { get; private set; } = Task.CompletedTask;

        public void OnProgress(EncodingProgress progress)
        {
            if (progress.ProcessId > 0 && Interlocked.Exchange(ref _watching, 1) == 0)
                Watcher = Task.Run(() => WatchAsync(progress.ProcessId));
        }

        private async Task WatchAsync(int processId)
        {
            try
            {
                using Process ffmpeg = Process.GetProcessById(processId);
                while (!ffmpeg.HasExited)
                {
                    string[] variantDirs = Directory.GetDirectories(outputDir);
                    if (
                        variantDirs.Length >= 2
                        && variantDirs.Min(d => Directory.GetFiles(d, "*.ts").Length)
                            >= segmentsWanted
                    )
                    {
                        Stopped = true;
                        ffmpeg.Kill(entireProcessTree: true);
                        return;
                    }

                    await Task.Delay(20);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                // the process had already exited
            }
        }

        public void OnStageStarted(string stageName) { }

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
