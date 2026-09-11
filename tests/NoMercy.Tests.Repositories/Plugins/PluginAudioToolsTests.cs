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
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.NmSystem.Information;
using NoMercy.Plugins;
using NoMercy.Plugins.Abstractions;
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

    // A key the derived store could have minted: 64 lowercase hex characters.
    // Anything shorter is refused before the store is consulted.
    private const string DerivedKey =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";
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
    private readonly Mock<IStorage> _derivedStorage = new();
    private readonly Mock<IDerivedAudioStore> _store = new();
    private readonly Mock<IFfmpegCapabilityProbe> _probe = new();
    private readonly Mock<IPluginMusicAnalysisWriter> _writer = new();
    private readonly Mock<IPluginMusicAnalysisWriterFactory> _writerFactory = new();

    private readonly EncoderOptions _encoderOptions = new() { FfmpegPathOverride = FfmpegBinary };

    private readonly List<PluginTrackStem> _registeredStems = [];
    private readonly List<string> _deletedDerivedPaths = [];

    // In lease order: the scratch file ffmpeg writes the vocals to, then the
    // one it writes the accompaniment to. The names carry a random Ulid, so
    // the argument-array assertions read them from here.
    private readonly List<string> _leasedDerivedPaths = [];
    private readonly List<string> _stdErrLines = [VersionLine];

    private string[] _capturedArguments = [];
    private string? _capturedWorkingDirectory;
    private string? _capturedExecutable;
    private int _runCount;
    private int _putCount;

    // In PutAsync order: the keys the mocked store minted and the content types
    // it was asked to store them under.
    private readonly List<string> _putKeys = [];
    private readonly List<string> _putContentTypes = [];

    // What the host did, in the order it did it: "touch" for a derived input's
    // keep-alive, "run" for the ffmpeg process itself.
    private readonly List<string> _callOrder = [];

    // Swapped out by the concurrency test for a task that only completes once
    // the second call has already been refused.
    private Task<ProcessResult> _runResult = Task.FromResult(
        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
    );

    public PluginAudioToolsTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        // The stem rows these tests register point at derived-store keys that
        // have no DerivedAudio parent row here - the store is a mock, so
        // nothing ever inserted one. The cascade itself is proven against a
        // real store in DerivedAudioStoreTests and against the schema in
        // AnalysisRecordModelTests; what is under test here is the command
        // ffmpeg is handed and the refusals around it.
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

        _derivedStorage
            .Setup(storage =>
                storage.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                (string path, CancellationToken _) =>
                {
                    _leasedDerivedPaths.Add(path);
                    return new LocalPathLease(path);
                }
            );
        _derivedStorage
            .Setup(storage =>
                storage.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns(Task.CompletedTask);
        _derivedStorage
            .Setup(storage =>
                storage.OpenReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(() => new MemoryStream([1, 2, 3]));
        _derivedStorage
            .Setup(storage =>
                storage.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(true);
        _derivedStorage
            .Setup(storage =>
                storage.DeleteAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .Returns(
                (string path, CancellationToken _) =>
                {
                    _deletedDerivedPaths.Add(path);
                    return Task.CompletedTask;
                }
            );

        _store
            .Setup(store => store.RelativePath(It.IsAny<string>()))
            .Returns((string key) => $"{key[..2]}/{key}");
        _store
            .Setup(store => store.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        _store
            .Setup(store => store.TouchAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(
                (string _, CancellationToken _) =>
                {
                    _callOrder.Add("touch");
                    return Task.CompletedTask;
                }
            );
        _store
            .Setup(store =>
                store.PutAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (Stream _, string contentType, CancellationToken _) =>
                {
                    string key = $"ab{++_putCount:D62}";
                    _putKeys.Add(key);
                    _putContentTypes.Add(contentType);
                    return new DerivedAudioEntry(key, contentType, 4096 + _putCount);
                }
            );

        _probe.Setup(probe => probe.GetCachedReport()).Returns(Report(stemsplitModelPresent: true));

        _writer
            .Setup(writer =>
                writer.RegisterStemAsync(It.IsAny<PluginTrackStem>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                (PluginTrackStem stem, CancellationToken _) =>
                {
                    _registeredStems.Add(stem);
                    return PluginWriteResult.Accepted();
                }
            );
        _writer
            .Setup(writer =>
                writer.RegisterStemsAsync(
                    It.IsAny<IReadOnlyList<PluginTrackStem>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (IReadOnlyList<PluginTrackStem> stems, CancellationToken _) =>
                {
                    _registeredStems.AddRange(stems);
                    return PluginWriteResult.Accepted();
                }
            );
        _writerFactory
            .Setup(factory => factory.CreateFor(It.IsAny<Ulid>()))
            .Returns(_writer.Object);

        _runner
            .Setup(runner =>
                runner.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<Action<string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                (
                    string executable,
                    string[] arguments,
                    Action<string>? onStdOut,
                    Action<string>? onStdErr,
                    string? workingDirectory,
                    CancellationToken _
                ) =>
                {
                    _runCount++;
                    _callOrder.Add("run");
                    _capturedExecutable = executable;
                    _capturedArguments = arguments;
                    _capturedWorkingDirectory = workingDirectory;

                    foreach (string line in _stdErrLines)
                    {
                        onStdErr?.Invoke(line);
                    }

                    onStdOut?.Invoke(string.Empty);

                    return _runResult;
                }
            );
    }

    private static CapabilityReport Report(bool stemsplitModelPresent) =>
        new(
            BluRayProtocol: true,
            DvdReadProtocol: true,
            AvailableEncoders: [],
            MissingFilters: [],
            MissingMuxers: [],
            FpcalcPresent: true,
            WhisperModelPresent: true,
            StemsplitModelPresent: stemsplitModelPresent,
            TesseractEngTraineddataPresent: true,
            TesseractModelsDirectory: null,
            Issues: []
        );

    private PluginAudioTools CreateTools(TimeSpan? runTimeout = null)
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
            _derivedStorage.Object,
            _store.Object,
            _writerFactory.Object,
            contextFactory.Object,
            _probe.Object
        )
        {
            RunTimeout = runTimeout ?? TimeSpan.FromMinutes(10),
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

    /// <summary>
    /// The whole stemsplit command, with the two scratch paths the run
    /// actually leased filled in - their names carry a random Ulid, so they
    /// cannot be written out literally.
    /// <para>
    /// Asserted in full, and deliberately: a window argument in the wrong
    /// place still satisfies "the array contains -t", which is how a window
    /// that reached only the first output went unnoticed.
    /// </para>
    /// </summary>
    private string[] ExpectedSplitArguments(params string[] windowArguments)
    {
        _leasedDerivedPaths.Should().HaveCount(2);

        string vocalsPath = _leasedDerivedPaths[0];
        string accompanimentPath = _leasedDerivedPaths[1];

        vocalsPath.Should().MatchRegex(@"^tmp/[0-9A-Z]{26}-vocals\.opus$");
        accompanimentPath.Should().Be(vocalsPath.Replace("-vocals.opus", "-accompaniment.opus"));

        return
        [
            "-nostdin",
            .. windowArguments,
            "-i",
            "/library/folder/track.flac",
            "-vn",
            "-sn",
            "-dn",
            "-filter_complex",
            $"[0:a]stemsplit=model={ModelFile}[voc][acc]",
            "-map",
            "[voc]",
            "-c:a",
            "libopus",
            "-b:a",
            "160k",
            "-ar",
            "48000",
            vocalsPath,
            "-map",
            "[acc]",
            "-c:a",
            "libopus",
            "-b:a",
            "160k",
            "-ar",
            "48000",
            accompanimentPath,
        ];
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

    [Fact]
    public async Task RunFilterGraph_Derived_ResolvesTheKeyThroughTheStore()
    {
        const string key = DerivedKey;

        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Derived(key),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        ArgumentAfter("-i").Should().Be($"ab/{key}");
    }

    /// <summary>
    /// A graph reading a derived file can run for ten minutes; the eviction
    /// sweep runs hourly and only spares what was used inside its grace
    /// window. The key is touched before ffmpeg is started, so the file cannot
    /// be evicted out from under the run that is reading it.
    /// </summary>
    [Fact]
    public async Task RunFilterGraph_OnADerivedInput_TouchesTheKeyFirst()
    {
        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Derived(DerivedKey),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        _callOrder.Should().Equal("touch", "run");
        _store.Verify(
            store => store.TouchAsync(DerivedKey, It.IsAny<CancellationToken>()),
            Times.Once
        );
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
    public async Task RunFilterGraph_RefusesAKeyThatIsNotInTheDerivedStore()
    {
        const string key = DerivedKey;
        _store
            .Setup(store => store.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Derived(key),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be($"storage key {key} is not in the derived store");
    }

    /// <summary>
    /// A derived key a plugin made up is refused in the same words as one the
    /// store does not hold, and the store is never asked - answering means
    /// slicing the key into a path, which is exactly what a traversal attempt
    /// is counting on.
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("../../etc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789")]
    public async Task RunFilterGraph_RefusesAMalformedDerivedKey(string key)
    {
        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Derived(key),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        result.Refusal.Should().Be($"storage key {key} is not in the derived store");
        _runCount.Should().Be(0);
        _store.Verify(
            store => store.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never
        );
        _store.Verify(store => store.RelativePath(It.IsAny<string>()), Times.Never);
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

    // --- SplitStemsAsync --------------------------------------------------

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
                    It.IsAny<CancellationToken>()
                )
            )
            .Returns(
                async (
                    string _,
                    string[] _,
                    Action<string>? _,
                    Action<string>? _,
                    string? _,
                    CancellationToken ct
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

    [Fact]
    public async Task SplitStems_Four_IsRefused()
    {
        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Four);

        result.Refusal.Should().Be("four-stem splitting is not available yet");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task SplitStems_MixIn_UsesTheFirstTwentyPercent()
    {
        await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.MixIn, PluginStemSet.Two);

        _capturedArguments.Should().Equal(ExpectedSplitArguments("-t", "60"));
    }

    [Fact]
    public async Task SplitStems_MixOut_StartsAtSeventyFivePercent()
    {
        await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.MixOut, PluginStemSet.Two);

        _capturedArguments.Should().Equal(ExpectedSplitArguments("-ss", "225"));
    }

    [Fact]
    public async Task SplitStems_Full_HasNoWindowArguments()
    {
        await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Two);

        _capturedArguments.Should().Equal(ExpectedSplitArguments());
        _capturedExecutable.Should().Be(FfmpegBinary);
        _capturedWorkingDirectory.Should().Be(AppFiles.FfmpegFolder);
    }

    [Fact]
    public async Task SplitStems_WindowIsAnInputOption_SoBothStemsGetIt()
    {
        await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.MixOut, PluginStemSet.Two);

        // The regression this guards: placed after -i, a window binds to the
        // next file named - the first output. The vocals stem would be the
        // window and the accompaniment stem the whole track, while both were
        // registered with the same window milliseconds. Ahead of -i it trims
        // the decoded stream, so both output pads see the same audio.
        int inputIndex = Array.IndexOf(_capturedArguments, "-i");
        inputIndex.Should().BePositive();
        Array.IndexOf(_capturedArguments, "-ss").Should().BeLessThan(inputIndex);
        _capturedArguments.Skip(inputIndex).Should().NotContain("-ss").And.NotContain("-t");
    }

    [Fact]
    public async Task SplitStems_RegistersBothStems_OnSuccess()
    {
        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.MixIn, PluginStemSet.Two);

        result.Refusal.Should().BeNull();
        result.Stems.Should().HaveCount(2);
        result.Stems.Select(stem => stem.Kind).Should().Equal("vocals", "accompaniment");
        result.Stems.Should().OnlyContain(stem => stem.Coverage == PluginStemCoverage.MixIn);
        result.Stems.Select(stem => stem.StorageKey).Should().OnlyHaveUniqueItems();

        // One write, both stems: a split whose second register row is refused
        // must not leave the first one behind, so the pair goes in together.
        _writer.Verify(
            writer =>
                writer.RegisterStemsAsync(
                    It.Is<IReadOnlyList<PluginTrackStem>>(stems =>
                        stems.Count == 2
                        && stems[0].Kind == "vocals"
                        && stems[1].Kind == "accompaniment"
                    ),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
        _writer.Verify(
            writer =>
                writer.RegisterStemAsync(
                    It.IsAny<PluginTrackStem>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );

        _registeredStems.Select(stem => stem.Kind).Should().Equal("vocals", "accompaniment");
        _registeredStems
            .Should()
            .OnlyContain(stem =>
                stem.TrackId == _trackId
                && stem.Format == "opus"
                && stem.SampleRate == 48000
                && stem.WindowStartMs == 0
                && stem.WindowEndMs == 60000
            );
        _registeredStems
            .Should()
            .OnlyContain(stem =>
                stem.ProducerVersion == AppFiles.StemsplitModel + "@9.0-NoMercy-MediaServer"
            );

        // The keys the mocked store minted, in the order it minted them: the
        // vocals stem must carry the first and the accompaniment the second.
        // Swapping them would still pass every assertion above while pointing
        // each register row at the other stem's audio.
        _registeredStems.Select(stem => stem.StorageKey).Should().Equal(_putKeys);
        result.Stems.Select(stem => stem.StorageKey).Should().Equal(_putKeys);

        // Opus in an Ogg container - the content type the derived store stores
        // the stem under, and the one a client is later handed it with.
        _putContentTypes.Should().Equal("audio/ogg", "audio/ogg");

        // Both temp files handed to the store are gone again afterwards.
        _deletedDerivedPaths.Should().HaveCount(2);
        _deletedDerivedPaths.Should().OnlyContain(path => path.StartsWith("tmp/"));
    }

    [Fact]
    public async Task SplitStems_RefusesWhenStemsplitExitsNonZero()
    {
        _runResult = Task.FromResult(
            new ProcessResult(3, string.Empty, string.Empty, TimeSpan.Zero)
        );

        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Two);

        result.Refusal.Should().Be("stemsplit exited with 3");
        result.Stems.Should().BeEmpty();
        _registeredStems.Should().BeEmpty();
        _deletedDerivedPaths.Should().HaveCount(2);
    }

    /// <summary>
    /// Exit code 0 is ffmpeg's word, not proof. A graph that separated nothing
    /// still exits clean, and the scratch files it never wrote would surface
    /// from inside the store as an I/O error rather than as something a plugin
    /// can act on.
    /// </summary>
    [Fact]
    public async Task SplitStems_RefusesWhenFfmpegWroteNoOutput()
    {
        _derivedStorage
            .Setup(storage =>
                storage.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(false);

        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Two);

        result.Refusal.Should().Be("stemsplit produced no output");
        result.Stems.Should().BeEmpty();
        _registeredStems.Should().BeEmpty();
        _store.Verify(
            store =>
                store.PutAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
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
                    It.IsAny<CancellationToken>()
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

        Func<Task<PluginStemSplitResult>> split = () =>
            tools.SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Two);

        PluginStemSplitResult splitResult = (await split.Should().NotThrowAsync()).Which;
        splitResult.Refusal.Should().Be("the server could not complete this call: IOException");
    }

    [Fact]
    public async Task SplitStems_RefusesWhenTheModelIsAbsent()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: false));

        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(_trackId.ToString(), PluginStemCoverage.Full, PluginStemSet.Two);

        result.Refusal.Should().Be("the stemsplit model is not installed");
        _runCount.Should().Be(0);
    }

    [Fact]
    public async Task SplitStems_RefusesATrackWithoutADuration()
    {
        PluginStemSplitResult result = await CreateTools()
            .SplitStemsAsync(
                _trackWithoutDurationId.ToString(),
                PluginStemCoverage.MixIn,
                PluginStemSet.Two
            );

        result.Refusal.Should().Be($"track {_trackWithoutDurationId} has no duration");
        _runCount.Should().Be(0);
    }
}
