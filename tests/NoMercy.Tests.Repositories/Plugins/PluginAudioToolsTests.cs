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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.NmSystem.Information;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The host side of <see cref="IPluginAudioTools" />: the exact ffmpeg command
/// each call builds, every refusal it can hand back instead, and that a
/// finished stem split lands both files in the derived store and both rows in
/// the stem register. ffmpeg itself is mocked - the runner records what it was
/// asked to run and replays stderr through the same callback the real process
/// runner writes to.
/// </summary>
public class PluginAudioToolsTests : IDisposable
{
    private const string FfmpegBinary = "ffmpeg";

    private const string VersionLine =
        "ffmpeg version 9.0-NoMercy-MediaServer Copyright (c) 2000-2026 the FFmpeg developers";

    private readonly SqliteConnection _connection;

    private readonly DbContextOptions<MediaContext> _options;

    private readonly Ulid _pluginId = Ulid.NewUlid();

    // Five minutes, so 20 % is 60 s and 75 % is 225 s - both whole seconds, so
    // the window arguments the tests assert on read without a decimal tail.
    private readonly Guid _trackId = Guid.NewGuid();

    // In the library, but the scan never recorded a file for it.
    private readonly Guid _trackWithoutFileId = Guid.NewGuid();

    // Has a file, but the library never recorded a duration.
    private readonly Guid _trackWithoutDurationId = Guid.NewGuid();

    private readonly Mock<IProcessRunner> _runner = new();

    private readonly Mock<IStorage> _storage = new();

    private readonly Mock<IStorageDriver> _driver = new();

    private readonly Mock<IFfmpegCapabilityProbe> _probe = new();

    private readonly EncoderOptions _encoderOptions = new() { FfmpegPathOverride = FfmpegBinary };

    private readonly List<string> _stdErrLines = [VersionLine];

    private string[] _capturedArguments = [];

    // Every run's arguments, in order, and the runs a test scripted one by one.
    private readonly List<string[]> _argumentsPerRun = [];

    private readonly Queue<ScriptedRun> _scriptedRuns = new();

    private string? _capturedWorkingDirectory;

    private string? _capturedExecutable;

    private int _runCount;

    // Swapped out by the concurrency test for a task that only completes once
    // the second call has already been refused.
    private Task<ProcessResult> _runResult = Task.FromResult(
        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
    );

    public PluginAudioToolsTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        // Tracks are seeded without the rows they would point at; what is
        // under test here is the command ffmpeg is handed and the refusals
        // around it.
        using (SqliteCommand foreignKeysOff = _connection.CreateCommand())
        {
            foreignKeysOff.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeysOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();

        context.Tracks.AddRange(
            new Track
            {
                Id = _trackId,
                Name = "Track A",
                Duration = "05:00",
                HostFolder = "/library/folder",
                Filename = "/track.flac",
            },
            new Track
            {
                Id = _trackWithoutFileId,
                Name = "Track B",
                Duration = "05:00",
                HostFolder = "",
                Filename = "",
            },
            new Track
            {
                Id = _trackWithoutDurationId,
                Name = "Track C",
                Duration = "",
                HostFolder = "/library/folder",
                Filename = "/other.flac",
            }
        );

        context.SaveChanges();

        SetUpMocks();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private void SetUpMocks()
    {
        // The ffmpeg binary is on disk unless a test says otherwise.
        _storage.Setup(storage => storage.Exists(It.IsAny<string>())).Returns(true);
        _storage
            .Setup(storage =>
                storage.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((string path, CancellationToken _) => new LocalPathLease(path));

        _driver
            .Setup(driver => driver.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns(
                (string parent, string[] segments) =>
                    parent.TrimEnd('/')
                    + "/"
                    + string.Join("/", segments.Select(segment => segment.Trim('/')))
            );

        _probe.Setup(probe => probe.GetCachedReport()).Returns(Report(stemsplitModelPresent: true));

        // The 8-arg overload with a kill signal is what RunFfmpegAsync always
        // calls now - see PluginAudioTools.RunFfmpegAsync for why (the
        // libgomp exit-hang workaround). Tests that need the kill signal or
        // the "progress=end" marker override this per-test; every other test
        // gets the same synchronous, successful run it always did.
        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Returns(
                (
                    string executable,
                    string[] arguments,
                    Action<string>? onStdOut,
                    Action<string>? onStdErr,
                    string? workingDirectory,
                    CancellationToken _,
                    CancellationToken _,
                    Action<int>? _
                ) =>
                {
                    _runCount++;
                    _capturedExecutable = executable;
                    _capturedArguments = arguments;
                    _capturedWorkingDirectory = workingDirectory;
                    _argumentsPerRun.Add(arguments);

                    // A test that scripts several runs gives each its own
                    // stderr and exit; every other test replays the same.
                    bool scripted = _scriptedRuns.TryDequeue(out ScriptedRun? run);

                    foreach (string line in scripted ? run!.StdErr : [.. _stdErrLines])
                    {
                        onStdErr?.Invoke(line);
                    }

                    onStdOut?.Invoke(string.Empty);

                    return scripted
                        ? Task.FromResult(
                            new ProcessResult(
                                run!.ExitCode,
                                string.Empty,
                                string.Empty,
                                TimeSpan.Zero
                            )
                        )
                        : _runResult;
                }
            );
    }

    private static CapabilityReport Report(
        bool stemsplitModelPresent,
        bool whisperModelPresent = true,
        bool vadModelPresent = true,
        bool lyricsAlignFilterPresent = true
    ) =>
        new(
            BluRayProtocol: true,
            DvdReadProtocol: true,
            AvailableEncoders: [],
            MissingFilters: [],
            MissingMuxers: [],
            FpcalcPresent: true,
            WhisperModelPresent: whisperModelPresent,
            StemsplitModelPresent: stemsplitModelPresent,
            TesseractEngTraineddataPresent: true,
            TesseractModelsDirectory: null,
            Issues: [],
            VadModelPresent: vadModelPresent,
            LyricsAlignFilterPresent: lyricsAlignFilterPresent
        );

    private PluginAudioTools CreateTools(
        TimeSpan? runTimeout = null,
        TimeSpan? exitGracePeriod = null,
        ILyricsAlignModelInstaller? alignModelInstaller = null,
        IStemsplitSourceSelector? stemsplitSources = null,
        TimeSpan? roformerNoChunkLimit = null,
        IRoformerGpuSlot? roformerGpuSlot = null
    )
    {
        Mock<IDbContextFactory<MediaContext>> contextFactory = new();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        PluginAudioTools tools = new(
            _pluginId,
            _encoderOptions,
            _runner.Object,
            _storage.Object,
            _driver.Object,
            contextFactory.Object,
            _probe.Object,
            stemsplitSources ?? new SpleeterOnlySources(),
            roformerGpuSlot ?? new RoformerGpuSlot(),
            dataFolderPath: Path.Combine(Path.GetTempPath(), "nm-plugin-data"),
            alignModelInstaller: alignModelInstaller
        )
        {
            RunTimeout = runTimeout ?? TimeSpan.FromMinutes(10),
            ExitGracePeriod = exitGracePeriod ?? TimeSpan.FromSeconds(2),
            RoformerNoChunkLimit = roformerNoChunkLimit ?? TimeSpan.FromMinutes(5),
        };

        return tools;
    }

    /// <summary>
    /// Fails rather than hanging the run when the thing it waits for never
    /// happens: a spin on a flag a broken change never sets is a test suite
    /// that stops instead of a test that reports.
    /// </summary>
    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"timed out after 5 seconds waiting for {what}");
            }

            await Task.Yield();
        }
    }

    private static string ModelFile => AppFiles.StemsplitModel + ".gguf";

    /// <summary>
    /// The value that follows the FIRST <paramref name="flag" /> in the
    /// captured argument array. Only safe for flags that appear once - the
    /// split command repeats <c>-map</c>, <c>-c:a</c> and the rest per output,
    /// which is why the split tests assert the whole array instead.
    /// </summary>
    private string? ArgumentAfter(string flag)
    {
        int index = Array.IndexOf(_capturedArguments, flag);
        return index >= 0 && index + 1 < _capturedArguments.Length
            ? _capturedArguments[index + 1]
            : null;
    }

    // --- RunFilterGraphAsync: the command --------------------------------

    [Fact]
    public async Task RunFilterGraph_Simple_BuildsTheAfArguments()
    {
        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("stereotools=mlev=0", Complex: false),
                null,
                null
            );

        result.Refusal.Should().BeNull();
        result.ExitCode.Should().Be(0);
        _capturedExecutable.Should().Be(FfmpegBinary);
        _capturedArguments
            .Should()
            .Equal(
                "-nostdin",
                "-i",
                "/library/folder/track.flac",
                "-vn",
                "-sn",
                "-dn",
                "-af",
                "stereotools=mlev=0",
                "-f",
                "null",
                "-"
            );
    }

    [Fact]
    public async Task RunFilterGraph_Complex_MapsTheInputToNull()
    {
        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]asplit[a][b]", Complex: true),
                null,
                null
            );

        _capturedArguments
            .Should()
            .Equal(
                "-nostdin",
                "-i",
                "/library/folder/track.flac",
                "-vn",
                "-sn",
                "-dn",
                "-filter_complex",
                "[0:a]asplit[a][b]",
                "-map",
                "0:a",
                "-f",
                "null",
                "-"
            );
    }

    [Fact]
    public async Task RunFilterGraph_ReplacesTheModelToken_AndRunsInTheFfmpegFolder()
    {
        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit=model={stemsModel}[voc][acc]", Complex: true),
                null,
                null
            );

        ArgumentAfter("-filter_complex").Should().Be($"[0:a]stemsplit=model={ModelFile}[voc][acc]");
        _capturedWorkingDirectory.Should().Be(AppFiles.FfmpegFolder);
    }

    // --- RunFilterGraphAsync: the refusals -------------------------------

    [Fact]
    public async Task RunFilterGraph_RefusesWhenTheStemsModelIsAbsent()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: false));

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit=model={stemsModel}[voc][acc]", Complex: true),
                null,
                null
            );

        result.Refusal.Should().Be("the stemsplit model is not installed");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesWhenTheWhisperModelIsAbsent()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: true, whisperModelPresent: false));

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("whisper=model={whisperModel}:destination=-", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be("the whisper model is not installed");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesWhenTheVadModelIsAbsent()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: true, vadModelPresent: false));

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("whisper=model={whisperModel}:vad_model={vadModel}", false),
                null,
                null,
                new PluginAudioRunOptions(TimeSpan.FromMinutes(30))
            );

        result.Refusal.Should().Be("the VAD model is not installed");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_WithOptions_ReplacesTheModelTokensAndRuns()
    {
        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("whisper=model={whisperModel}:vad_model={vadModel}", false),
                null,
                null,
                new PluginAudioRunOptions(TimeSpan.FromMinutes(30))
            );

        ArgumentAfter("-af")
            .Should()
            .Be($"whisper=model={AppFiles.WhisperModel}.bin:vad_model={AppFiles.VadModel}.bin");
        _runCount.Should().Be(1);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesAPluginFileOutsideTheDataFolder()
    {
        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("whisper=prompt_file={pluginFile:../secret.txt}", false),
                null,
                null
            );

        result.Refusal.Should().Contain("leaves the plugin's data folder");
        _runCount.Should().Be(0);
    }

    [Theory]
    [InlineData(null, 10)]
    [InlineData(0, 10)]
    [InlineData(30, 30)]
    [InlineData(90, 60)]
    public void EffectiveTimeout_IsTheDefault_TheRequest_OrTheMaximum(
        int? requestedMinutes,
        int expectedMinutes
    )
    {
        PluginAudioRunOptions? options = requestedMinutes is { } minutes
            ? new(TimeSpan.FromMinutes(minutes))
            : null;

        TimeSpan effective = PluginAudioTools.EffectiveTimeout(options, TimeSpan.FromMinutes(10));

        effective.Should().Be(TimeSpan.FromMinutes(expectedMinutes));
    }

    [Fact]
    public async Task RunFilterGraph_RefusesASecondConcurrentRun()
    {
        TaskCompletionSource<ProcessResult> pending = new();
        _runResult = pending.Task;

        PluginAudioTools tools = CreateTools();

        Task<PluginAudioRunResult> first = tools.RunFilterGraphAsync(
            PluginAudioInput.Track(_trackId.ToString()),
            new PluginFilterGraph("volume=1", Complex: false),
            null,
            null
        );

        // The first run has to have reached the runner before the second call
        // means anything; the mock records every entry, so wait for that.
        await WaitForAsync(() => _runCount > 0, "the first ffmpeg run to reach the runner");

        PluginAudioRunResult second = await tools.RunFilterGraphAsync(
            PluginAudioInput.Track(_trackId.ToString()),
            new PluginFilterGraph("volume=1", Complex: false),
            null,
            null
        );

        second.Refusal.Should().Be("another ffmpeg run of this plugin is still in progress");

        pending.SetResult(new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero));
        (await first).Refusal.Should().BeNull();
    }

    [Fact]
    public async Task RunFilterGraph_RefusesAnUnknownTrack()
    {
        Guid unknownTrackId = Guid.NewGuid();

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(unknownTrackId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be($"track {unknownTrackId} has no file");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesATrackWithoutAFile()
    {
        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackWithoutFileId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be($"track {_trackWithoutFileId} has no file");
    }

    [Fact]
    public async Task RunFilterGraph_RefusesWhenFfmpegIsNotInstalled()
    {
        _encoderOptions.FfmpegPathOverride = null;

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be("ffmpeg is not installed");
    }

    /// <summary>
    /// ffmpeg on a stalled mount never returns on its own, so the host puts its
    /// own clock on every run. The result is -2 rather than a refusal: the run
    /// did start, the plugin's graph was accepted, and its callbacks saw
    /// whatever ffmpeg printed before the clock ran out.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_ReturnsMinusTwo_WhenTheRunTimesOut()
    {
        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Returns(
                async (
                    string _,
                    string[] _,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken ct,
                    CancellationToken _,
                    Action<int>? _
                ) =>
                {
                    // The real runner kills the process when its token trips;
                    // waiting forever on the token is the same observable thing.
                    await Task.Delay(-1, ct);
                    return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero);
                }
            );

        PluginAudioRunResult result = await CreateTools(TimeSpan.FromMilliseconds(50))
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.ExitCode.Should().Be(-2);
        result.Refusal.Should().BeNull();
    }

    /// <summary>
    /// The build token goes into every stem's producer version, so a model or
    /// binary upgrade can be spotted the way an analyzer upgrade is. A banner
    /// that is not the one ffmpeg usually prints still has to produce a marker.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Stream mapping:")]
    [InlineData("ffmpeg version ")]
    public void FfmpegVersion_IsUnknown_WhenTheBannerCarriesNoToken(string? bannerLine)
    {
        PluginAudioArguments.FfmpegVersion(bannerLine).Should().Be("unknown");
    }

    [Fact]
    public void FfmpegVersion_ReadsTheTokenOutOfARealBanner()
    {
        PluginAudioArguments.FfmpegVersion(VersionLine).Should().Be("9.0-NoMercy-MediaServer");
    }

    /// <summary>
    /// A plugin sweeping a library unattended must not lose the whole sweep to
    /// one unlucky track: a dependency that throws where nothing planned for it
    /// becomes a refusal naming the exception's type.
    /// </summary>
    [Fact]
    public async Task AThrowingDependency_BecomesARefusal()
    {
        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .ThrowsAsync(new IOException("the library volume went away"));

        PluginAudioTools tools = CreateTools();

        Func<Task<PluginAudioRunResult>> runGraph = () =>
            tools.RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        PluginAudioRunResult graphResult = (await runGraph.Should().NotThrowAsync()).Which;
        graphResult.Refusal.Should().Be("the server could not complete this call: IOException");
    }

    // --- {alignModel}: the lyricsalign filter, its model, its phoneme tables ---

    /// <summary>The graph the lyrics aligner runs: stems first, then the known text placed on them.</summary>
    private static readonly PluginFilterGraph AlignGraph = new(
        "[0:a]stemsplit=model={stemsModel}:stem=all[v][b];"
            + "[v][b]lyricsalign=model={alignModel}:language=en:threads=0:destination=-",
        Complex: true
    );

    private Task<PluginAudioRunResult> RunAlignGraph(
        ILyricsAlignModelInstaller? alignModelInstaller = null
    ) =>
        CreateTools(alignModelInstaller: alignModelInstaller)
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                AlignGraph,
                null,
                null
            );

    [Fact]
    public async Task RunFilterGraph_WithTheAlignModel_ReplacesTheTokenAndRuns()
    {
        PluginAudioRunResult result = await RunAlignGraph();

        result.Refusal.Should().BeNull();
        ArgumentAfter("-filter_complex")
            .Should()
            .Contain($"lyricsalign=model={AppFiles.LyricsAlignModel}.gguf:")
            .And.NotContain("{alignModel}");
    }

    [Fact]
    public async Task RunFilterGraph_RefusesTheAlignModel_OnAnFfmpegWithoutTheFilter_NamingTheRelease()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: true, lyricsAlignFilterPresent: false));

        PluginAudioRunResult result = await RunAlignGraph();

        result
            .Refusal.Should()
            .Be(
                "this server's ffmpeg has no lyricsalign filter; it needs nomercy-ffmpeg v1.0.46 or later"
            );
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesTheAlignModel_WhileTheFileIsAbsent()
    {
        _storage.Setup(storage => storage.Exists(AppFiles.LyricsAlignModelPath)).Returns(false);

        PluginAudioRunResult result = await RunAlignGraph();

        result.Refusal.Should().Be("the lyricsalign model is not installed");
        _runCount.Should().Be(0);
    }

    /// <summary>
    /// The model is not fetched at setup: it is 600 MB and only a plugin
    /// that aligns lyrics reads it. The first graph that names it is what
    /// asks for it - refused, with the download started.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_AsksForTheAlignModel_TheFirstTimeAGraphNamesIt()
    {
        _storage.Setup(storage => storage.Exists(AppFiles.LyricsAlignModelPath)).Returns(false);
        Mock<ILyricsAlignModelInstaller> installer = new();

        PluginAudioRunResult result = await RunAlignGraph(installer.Object);

        result
            .Refusal.Should()
            .Be(
                "the lyricsalign model is not installed yet; the server has started downloading it, ask again in a few minutes"
            );
        installer.Verify(i => i.RequestInstall(), Times.Once);
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task RunFilterGraph_DoesNotAskForTheAlignModel_WhenItIsThere_OrWhenNoGraphNamesIt()
    {
        Mock<ILyricsAlignModelInstaller> installer = new();

        (await RunAlignGraph(installer.Object)).Refusal.Should().BeNull();

        _storage.Setup(storage => storage.Exists(AppFiles.LyricsAlignModelPath)).Returns(false);
        PluginAudioRunResult plain = await CreateTools(alignModelInstaller: installer.Object)
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        plain.Refusal.Should().BeNull("a graph that does not name the model does not need it");
        installer.Verify(i => i.RequestInstall(), Times.Never);
    }

    [Fact]
    public async Task RunFilterGraph_RefusesTheAlignModel_WithoutTheEspeakData()
    {
        _storage.Setup(storage => storage.Exists(AppFiles.EspeakDataMarkerPath)).Returns(false);

        PluginAudioRunResult result = await RunAlignGraph();

        result
            .Refusal.Should()
            .Be(
                "the espeak-ng data is missing from the ffmpeg folder; it ships with nomercy-ffmpeg v1.0.46 and later"
            );
        _runCount.Should().Be(0);
    }

    /// <summary>
    /// The probe runs once, some minutes after start, and the model arrives
    /// long after it. Neither a missing report nor a stale one may refuse a
    /// graph whose files are on disk.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RunFilterGraph_WithTheAlignModel_TrustsTheDiskOverTheReport(bool staleReport)
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(
                staleReport
                    ? Report(stemsplitModelPresent: true) with
                    {
                        LyricsAlignModelPresent = false,
                        EspeakDataPresent = false,
                    }
                    : null
            );

        (await RunAlignGraph()).Refusal.Should().BeNull();
        _runCount.Should().Be(1);
    }

    // --- RoFormer, and the fall back to Spleeter (#637) -------------------

    /// <summary>One ffmpeg run a test scripted: its exit code and its stderr.</summary>
    private sealed record ScriptedRun(int ExitCode, string[] StdErr);

    // What ffmpeg on Windows exits with after the filter refused (-22 as a
    // byte); the host keys on the filter's line, not on this number.
    private const int RefusedExit = 234;

    private readonly Mock<IStemsplitSourceSelector> _sources = new();

    private IStemsplitSourceSelector RoformerWhereAsked()
    {
        _sources
            .Setup(sources =>
                sources.ChooseAsync(It.IsAny<RoformerPreset>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                (RoformerPreset preset, CancellationToken _) => StemsplitSource.Roformer(preset)
            );
        return _sources.Object;
    }

    [Fact]
    public async Task RunFilterGraph_TheVocalSourceToken_BecomesPresetVocalFull()
    {
        await CreateTools(stemsplitSources: RoformerWhereAsked())
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph(
                    "[0:a]stemsplit={stemsVocalSource}:stem=all[v][b];[v][b]amix",
                    Complex: true
                ),
                null,
                null
            );

        ArgumentAfter("-filter_complex")
            .Should()
            .Be("[0:a]stemsplit=preset=vocal_full:cpu_fallback=0:stem=all[v][b];[v][b]amix");
        _sources.Verify(
            sources => sources.ChooseAsync(RoformerPreset.Full, It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    /// <summary>
    /// The plugin is meant to see one run: the refused one's lines are held
    /// back and dropped, the Spleeter run's handed on.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_ARefusedRoformerRun_RunsAgainWithSpleeter_AndThePluginSeesOneRun()
    {
        _scriptedRuns.Enqueue(
            new(
                RefusedExit,
                [
                    StemsplitLogFixtures.Banner,
                    StemsplitLogFixtures.ModelLoaded,
                    StemsplitLogFixtures.NoGpuRefusal,
                ]
            )
        );
        _scriptedRuns.Enqueue(new(0, [VersionLine, StemsplitLogFixtures.SpleeterLoaded]));
        List<string> stdErr = [];
        List<string> stdOut = [];

        PluginAudioRunResult result = await CreateTools(stemsplitSources: RoformerWhereAsked())
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                stdOut.Add,
                stdErr.Add
            );

        result.ExitCode.Should().Be(0);
        _argumentsPerRun.Should().HaveCount(2);
        _argumentsPerRun[1].Should().Contain($"[0:a]stemsplit=model={ModelFile}:stem=all[v][b]");
        stdErr.Should().Equal(VersionLine, StemsplitLogFixtures.SpleeterLoaded);
        stdOut.Should().Equal(string.Empty);
        _sources.Verify(
            sources =>
                sources.RememberRefusalAsync(
                    It.IsAny<IReadOnlyCollection<RoformerPreset>>(),
                    StemsplitLogFixtures.NoGpuRefusal,
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task RunFilterGraph_ARoformerRunThatStarted_HandsOnEveryLineInOrder()
    {
        string[] lines =
        [
            StemsplitLogFixtures.Banner,
            StemsplitLogFixtures.ModelLoaded,
            StemsplitLogFixtures.RunsOn,
            StemsplitLogFixtures.FirstChunk,
            StemsplitLogFixtures.LastChunk,
        ];
        _scriptedRuns.Enqueue(new(0, lines));
        List<string> stdErr = [];

        await CreateTools(stemsplitSources: RoformerWhereAsked())
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}[v][b]", true),
                null,
                stdErr.Add
            );

        stdErr.Should().Equal(lines);
        _runCount.Should().Be(1);
    }

    /// <summary>
    /// RoFormer puts the GPU backend of the whole process at full precision,
    /// and whisper on that GPU runs several times slower: a graph that
    /// transcribes splits with Spleeter.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_AGraphThatTranscribes_GetsSpleeter_WithoutAskingForRoformer()
    {
        await CreateTools(stemsplitSources: RoformerWhereAsked())
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph(
                    "[0:a]stemsplit={stemsSource}:stem=vocals,aresample=16000,whisper=model={whisperModel}",
                    true
                ),
                null,
                null
            );

        ArgumentAfter("-filter_complex")
            .Should()
            .StartWith($"[0:a]stemsplit=model={ModelFile}:stem=vocals,");
        _sources.Verify(
            sources =>
                sources.ChooseAsync(It.IsAny<RoformerPreset>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
    }

    [Fact]
    public async Task RunFilterGraph_TheStemsModelToken_StaysSpleeter_OnARoformerMachine()
    {
        await CreateTools(stemsplitSources: RoformerWhereAsked())
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit=model={stemsModel}[v][b]", true),
                null,
                null
            );

        ArgumentAfter("-filter_complex").Should().Be($"[0:a]stemsplit=model={ModelFile}[v][b]");
    }

    // --- RoFormer: a time-out that follows the filter's progress (#637) -----

    /// <summary>
    /// ffmpeg that prints <paramref name="lines" /> one by one, <paramref name="gap" />
    /// apart, and then either exits 0 or, with <paramref name="hangAfter" />,
    /// waits until the host ends it - the way a stuck process does.
    /// </summary>
    private void FfmpegPrinting(string[] lines, TimeSpan gap, bool hangAfter)
    {
        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Returns(
                async (
                    string _,
                    string[] arguments,
                    Action<string>? _,
                    Action<string>? onStdErr,
                    string? _,
                    CancellationToken ct,
                    CancellationToken kill,
                    Action<int>? _
                ) =>
                {
                    _runCount++;
                    _capturedArguments = arguments;
                    using CancellationTokenSource ended =
                        CancellationTokenSource.CreateLinkedTokenSource(ct, kill);

                    foreach (string line in lines)
                    {
                        await Task.Delay(gap, ended.Token);
                        onStdErr?.Invoke(line);
                    }

                    if (hangAfter)
                        await Task.Delay(Timeout.InfiniteTimeSpan, ended.Token);

                    return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero);
                }
            );
    }

    private Task<PluginAudioRunResult> RunRoformerGraph(
        PluginAudioTools tools,
        PluginAudioRunOptions? options = null
    ) =>
        options is null
            ? tools.RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                null,
                null
            )
            : tools.RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                null,
                null,
                options
            );

    [Fact]
    public async Task RunFilterGraph_ARoformerRunWithoutAChunkLine_IsEndedAsStalled()
    {
        FfmpegPrinting(
            [StemsplitLogFixtures.Banner, StemsplitLogFixtures.ModelLoaded],
            TimeSpan.FromMilliseconds(5),
            hangAfter: true
        );

        PluginAudioRunResult result = await RunRoformerGraph(
            CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerNoChunkLimit: TimeSpan.FromMilliseconds(150)
            )
        );

        result.ExitCode.Should().Be(PluginAudioRunResult.StalledExitCode);
        result.Refusal.Should().BeNull();
    }

    /// <summary>
    /// The plugin asked for a total that a slow card cannot keep. While the
    /// filter keeps handing out chunks the run goes on past it.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_ARoformerRunThatKeepsHandingOutChunks_OutlivesThePluginsTimeout()
    {
        string[] chunks =
        [
            .. Enumerable
                .Range(1, 8)
                .Select(chunk =>
                    $"[Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 1/1, chunk {chunk}/? at sample 0 (485100 samples, 485376 received), 3.0 s elapsed, rtf 1.30."
                ),
            "[Parsed_stemsplit_0 @ 000001f73aecab40] stemsplit: model 1/1, chunk 9/9 at sample 1719900 (485100 samples, 2205000 received), 38.9 s elapsed, rtf 1.27.",
        ];
        FfmpegPrinting(chunks, TimeSpan.FromMilliseconds(60), hangAfter: false);

        PluginAudioRunResult result = await RunRoformerGraph(
            CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerNoChunkLimit: TimeSpan.FromSeconds(2)
            ),
            new PluginAudioRunOptions(TimeSpan.FromMilliseconds(150))
        );

        result.ExitCode.Should().Be(0);
    }

    /// <summary>Once the split is done, the rest of the run has the plugin's own limit.</summary>
    [Fact]
    public async Task RunFilterGraph_AfterTheLastChunk_ThePluginsTimeoutHoldsAgain()
    {
        FfmpegPrinting(
            [StemsplitLogFixtures.FirstChunk, StemsplitLogFixtures.LastChunk],
            TimeSpan.FromMilliseconds(5),
            hangAfter: true
        );

        PluginAudioRunResult result = await RunRoformerGraph(
            CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerNoChunkLimit: TimeSpan.FromSeconds(30)
            ),
            new PluginAudioRunOptions(TimeSpan.FromMilliseconds(150))
        );

        result.ExitCode.Should().Be(PluginAudioRunResult.TimedOutExitCode);
    }

    /// <summary>
    /// A graph with both source tokens runs two RoFormer filters. The first
    /// one's last chunk does not end the split: the second is still judged
    /// by its chunk lines, not by the plugin's short total.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_WithTwoRoformerFilters_TheFirstOnesLastChunkDoesNotEndTheSplit()
    {
        FfmpegPrinting(
            StemsplitLogFixtures.TwoFilters[..8],
            TimeSpan.FromMilliseconds(5),
            hangAfter: true
        );

        PluginAudioRunResult result = await CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerNoChunkLimit: TimeSpan.FromMilliseconds(400)
            )
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph(
                    "[0:a]asplit[a][b];[a]stemsplit={stemsVocalSource}:stem=vocals[v1];[b]stemsplit={stemsSource}:stem=vocals[v2]",
                    true
                ),
                null,
                null,
                new PluginAudioRunOptions(TimeSpan.FromMilliseconds(100))
            );

        result.ExitCode.Should().Be(PluginAudioRunResult.StalledExitCode);
    }

    /// <summary>Spleeter prints no chunk lines, so its runs keep their fixed limit.</summary>
    [Fact]
    public async Task RunFilterGraph_ASpleeterRun_IsNotHeldToChunkLines()
    {
        FfmpegPrinting(
            [StemsplitLogFixtures.Banner, StemsplitLogFixtures.SpleeterLoaded],
            TimeSpan.FromMilliseconds(5),
            hangAfter: false
        );

        PluginAudioRunResult result = await CreateTools(
                roformerNoChunkLimit: TimeSpan.FromMilliseconds(1)
            )
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                null,
                null
            );

        result.ExitCode.Should().Be(0);
    }

    // --- RoFormer: one run on the GPU at a time (#637) ----------------------

    /// <summary>
    /// ffmpeg whose first run blocks until <paramref name="firstRun" />
    /// completes, counting how many runs it has going at once.
    /// </summary>
    private void FfmpegHoldingItsFirstRun(Task firstRun, Action<int> started, Action<int> atOnce)
    {
        int count = 0;
        int running = 0;

        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<CancellationToken>(),
                    It.IsAny<Action<int>?>()
                )
            )
            .Returns(
                async (
                    string _,
                    string[] _,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken _,
                    CancellationToken _,
                    Action<int>? _
                ) =>
                {
                    int number = Interlocked.Increment(ref count);
                    atOnce(Interlocked.Increment(ref running));
                    started(number);

                    if (number == 1)
                        await firstRun;

                    Interlocked.Decrement(ref running);
                    return new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero);
                }
            );
    }

    [Fact]
    public async Task TwoPlugins_NeverRunRoformerOnTheGpuAtTheSameTime()
    {
        TaskCompletionSource firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        int mostAtOnce = 0;
        FfmpegHoldingItsFirstRun(
            firstRun.Task,
            number => started = number,
            running => mostAtOnce = Math.Max(mostAtOnce, running)
        );
        RoformerGpuSlot slot = new();

        Task<PluginAudioRunResult> onePlugin = CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerGpuSlot: slot
            )
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                null,
                null
            );
        await WaitForAsync(() => started == 1, "the first plugin's split to start");

        Task<PluginAudioRunResult> otherPlugin = CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerGpuSlot: slot
            )
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsVocalSource}:stem=all[v][b]", true),
                null,
                null
            );
        await Task.Delay(100);

        started.Should().Be(1, "the second RoFormer run waits for the GPU");

        firstRun.SetResult();
        (await onePlugin).ExitCode.Should().Be(0);
        (await otherPlugin).ExitCode.Should().Be(0);
        started.Should().Be(2);
        mostAtOnce.Should().Be(1);
    }

    [Fact]
    public async Task ASpleeterRun_DoesNotWaitForTheGpu()
    {
        TaskCompletionSource firstRun = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started = 0;
        FfmpegHoldingItsFirstRun(firstRun.Task, number => started = number, _ => { });
        RoformerGpuSlot slot = new();

        Task<PluginAudioRunResult> roformer = CreateTools(
                stemsplitSources: RoformerWhereAsked(),
                roformerGpuSlot: slot
            )
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit={stemsSource}:stem=all[v][b]", true),
                null,
                null
            );
        await WaitForAsync(() => started == 1, "the RoFormer split to start");

        PluginAudioRunResult spleeter = await CreateTools(roformerGpuSlot: slot)
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackId.ToString()),
                new PluginFilterGraph("[0:a]stemsplit=model={stemsModel}[v][b]", true),
                null,
                null
            );

        spleeter.ExitCode.Should().Be(0);
        started.Should().Be(2);

        firstRun.SetResult();
        await roformer;
    }
}
