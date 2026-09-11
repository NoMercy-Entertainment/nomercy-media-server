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

    // Swapped out by the concurrency test for a task that only completes once
    // the second call has already been refused.
    private Task<ProcessResult> _runResult = Task.FromResult(
        new ProcessResult(0, string.Empty, string.Empty, TimeSpan.Zero)
    );

    public PluginAudioToolsTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

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
            .Setup(store =>
                store.PutAsync(
                    It.IsAny<Stream>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                (Stream _, string contentType, CancellationToken _) =>
                    new DerivedAudioEntry($"ab{++_putCount:D62}", contentType, 4096 + _putCount)
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

    private PluginAudioTools CreateTools()
    {
        Mock<IDbContextFactory<MediaContext>> contextFactory = new();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new PluginAudioTools(
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
        );
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
        const string key = "abcdef0123456789";

        await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Derived(key),
                new PluginFilterGraph("volume=1", Complex: false),
                null,
                null
            );

        ArgumentAfter("-i").Should().Be($"ab/{key}");
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
        while (_runCount == 0)
        {
            await Task.Yield();
        }

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
        const string key = "abcdef0123456789";
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
