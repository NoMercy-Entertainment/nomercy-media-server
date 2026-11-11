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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Startup;
using NoMercy.MediaProcessing.DerivedAudio;
using NoMercy.PluginSdk;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.PluginSdk.Quotas;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// <see cref="IPluginAudioTools.RenderAsync" /> on the host: the command it
/// builds, what it refuses before ffmpeg starts, and what is left in the
/// plugin's data folder after every way a run can end.
/// <para>
/// The data folder is a real folder on disk, because what is under test is
/// what is in it afterwards. ffmpeg is a fake that writes bytes to the
/// output paths it was handed, prints what a test tells it to and ends the
/// way a test tells it to.
/// </para>
/// </summary>
public class PluginAudioRenderTests : IDisposable
{
    private const string FfmpegBinary = "ffmpeg";

    private static readonly PluginAudioRunOptions DefaultOptions = new(null);

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _pluginId = Ulid.NewUlid();
    private readonly Guid _trackA = Guid.NewGuid();
    private readonly Guid _trackB = Guid.NewGuid();

    private readonly string _dataFolder = Path.Combine(
        Path.GetTempPath(),
        $"nm-render-{Guid.NewGuid():N}"
    );

    private readonly LocalStorageDriver _driver = new();
    private readonly Mock<IProcessRunner> _runner = new();
    private readonly Mock<IStorage> _storage = new();
    private readonly Mock<IFfmpegCapabilityProbe> _probe = new();
    private readonly EncoderOptions _encoderOptions = new() { FfmpegPathOverride = FfmpegBinary };

    // Every command the fake ffmpeg was started with, in order.
    private readonly List<string[]> _runs = [];

    // What the fake ffmpeg does once started. The default writes every
    // output, closes its progress report and exits 0.
    private Func<FfmpegCall, Task<ProcessResult>> _ffmpeg = call =>
    {
        call.WriteOutputs(1000);
        call.StdOut?.Invoke("progress=end");
        return Task.FromResult(Exited(0));
    };

    public PluginAudioRenderTests()
    {
        Directory.CreateDirectory(_dataFolder);

        _connection = new("Data Source=:memory:");
        _connection.Open();

        // The two track rows below stand alone: no folder row, no album.
        // What is under test is the command and the files, not the schema.
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
                Id = _trackA,
                Name = "Track A",
                Duration = "05:00",
                HostFolder = "/library/folder",
                Filename = "/track-a.flac",
            },
            new Track
            {
                Id = _trackB,
                Name = "Track B",
                Duration = "04:00",
                HostFolder = "/library/folder",
                Filename = "/track-b.flac",
            }
        );
        context.SaveChanges();

        _storage.Setup(storage => storage.Exists(It.IsAny<string>())).Returns(true);
        _storage
            .Setup(storage =>
                storage.AcquireLocalPathAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync((string path, CancellationToken _) => new LocalPathLease(path));

        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: true, whisperModelPresent: true));

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
                    string _,
                    string[] arguments,
                    Action<string>? onStdOut,
                    Action<string>? onStdErr,
                    string? _,
                    CancellationToken cancelled,
                    CancellationToken kill,
                    Action<int>? _
                ) =>
                {
                    _runs.Add(arguments);
                    return _ffmpeg(new(arguments, onStdOut, onStdErr, cancelled, kill));
                }
            );
    }

    public void Dispose()
    {
        _connection.Dispose();

        if (Directory.Exists(_dataFolder))
            Directory.Delete(_dataFolder, recursive: true);

        GC.SuppressFinalize(this);
    }

    // --- the harness ------------------------------------------------------

    /// <summary>One start of the fake ffmpeg: what it was asked to run and how it can answer.</summary>
    private sealed record FfmpegCall(
        string[] Arguments,
        Action<string>? StdOut,
        Action<string>? StdErr,
        CancellationToken Cancelled,
        CancellationToken Kill
    )
    {
        /// <summary>The files ffmpeg was told to write, in command order.</summary>
        public IReadOnlyList<string> OutputPaths => Arguments.Where(IsTemporaryOutput).ToList();

        /// <summary>Writes every output, the first <paramref name="bytes" /> long and each next one as much longer.</summary>
        public void WriteOutputs(int bytes)
        {
            for (int index = 0; index < OutputPaths.Count; index++)
                File.WriteAllBytes(OutputPaths[index], new byte[bytes * (index + 1)]);
        }

        /// <summary>Hangs the way a stuck process does, until the host ends it.</summary>
        public async Task<ProcessResult> HangAsync()
        {
            using CancellationTokenSource ended = CancellationTokenSource.CreateLinkedTokenSource(
                Cancelled,
                Kill
            );
            await Task.Delay(Timeout.InfiniteTimeSpan, ended.Token);
            return Exited(0);
        }
    }

    private static bool IsTemporaryOutput(string argument) =>
        Path.GetFileName(argument)
            .StartsWith(PluginAudioRenderPlan.TempPrefix, StringComparison.Ordinal);

    private static ProcessResult Exited(int exitCode) =>
        new(exitCode, string.Empty, string.Empty, TimeSpan.Zero);

    private static CapabilityReport Report(bool stemsplitModelPresent, bool whisperModelPresent) =>
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
            VadModelPresent: true
        );

    private PluginAudioTools CreateTools(
        TimeSpan? runTimeout = null,
        TimeSpan? exitGracePeriod = null,
        TimeSpan? quotaCheckInterval = null,
        PluginQuotaMeter? quotas = null
    )
    {
        Mock<IDbContextFactory<MediaContext>> contextFactory = new();
        contextFactory
            .Setup(factory => factory.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new(
            _pluginId,
            _encoderOptions,
            _runner.Object,
            _storage.Object,
            _driver,
            Mock.Of<IStorage>(),
            Mock.Of<IDerivedAudioStore>(),
            Mock.Of<IPluginMusicAnalysisWriterFactory>(),
            contextFactory.Object,
            _probe.Object,
            dataFolderPath: _dataFolder,
            quotas: quotas
        )
        {
            RunTimeout = runTimeout ?? TimeSpan.FromMinutes(10),
            ExitGracePeriod = exitGracePeriod ?? TimeSpan.FromSeconds(2),
            QuotaCheckInterval = quotaCheckInterval ?? TimeSpan.FromSeconds(2),
            // No floor under the stall limit here, so a test can prove the
            // limit in milliseconds. The floor itself is proven on the limits.
            ShortestStallLimit = TimeSpan.Zero,
        };
    }

    private static PluginQuotaMeter Meter(long diskBytes) =>
        new(new FixedQuota(diskBytes), TimeProvider.System);

    private sealed class FixedQuota(long diskBytes) : IPluginQuotaSource
    {
        public PluginQuota For(Ulid pluginId) =>
            new(50, 512 * 1024 * 1024, diskBytes, long.MaxValue);
    }

    private PluginAudioSource TrackA() => new(PluginAudioInput.Track(_trackA.ToString()));

    private PluginAudioSource TrackB() => new(PluginAudioInput.Track(_trackB.ToString()));

    /// <summary>The smallest render there is: one track, through one filter, to one file.</summary>
    private PluginAudioRender OneOutput(string relativePath = "out.wav") =>
        new([TrackA()], "[0:a]anull[out]", [new("out", relativePath, PluginAudioCodec.Wav)]);

    private string LibraryPath(string filename) => _driver.CombinePath("/library/folder", filename);

    private string InDataFolder(string relativePath) =>
        Path.Combine(_dataFolder, relativePath.Replace('/', Path.DirectorySeparatorChar));

    private void WriteDataFile(string relativePath, int bytes)
    {
        string path = InDataFolder(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Enumerable.Repeat((byte)7, bytes).ToArray());
    }

    /// <summary>Every file in the data folder, relative, with forward slashes, sorted.</summary>
    private List<string> FilesInDataFolder() =>
        Directory
            .EnumerateFiles(_dataFolder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_dataFolder, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private List<string> FoldersInDataFolder() =>
        Directory
            .EnumerateDirectories(_dataFolder, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(_dataFolder, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)
            .ToList();

    private static async Task WaitForAsync(Func<bool> condition, string what)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"timed out after 5 seconds waiting for {what}");

            await Task.Delay(5);
        }
    }

    // --- the command ------------------------------------------------------

    [Fact]
    public async Task Render_OneInputOneOutput_BuildsTheWholeCommand()
    {
        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]loudnorm[out]",
                    [new("out", "previews/a.opus", PluginAudioCodec.Opus, 96, 48000)]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().BeNull();
        result.ExitCode.Should().Be(0);

        string[] arguments = _runs.Should().ContainSingle().Subject;
        string temporary = arguments[^1];

        // Asserted in full: an option in the wrong place still satisfies
        // "the array contains it", and ffmpeg binds an option by its place.
        arguments
            .Should()
            .Equal(
                "-nostdin",
                "-nostats",
                "-progress",
                "pipe:1",
                "-stats_period",
                "5",
                "-i",
                LibraryPath("/track-a.flac"),
                "-vn",
                "-sn",
                "-dn",
                "-filter_complex",
                "[0:a]loudnorm[out]",
                "-map",
                "[out]",
                "-c:a",
                "libopus",
                "-b:a",
                "96k",
                "-ar",
                "48000",
                temporary
            );

        // In the folder the file ends up in, under a name no finished file
        // has, with the extension ffmpeg picks the container from.
        Path.GetDirectoryName(temporary).Should().Be(InDataFolder("previews"));
        Path.GetFileName(temporary).Should().MatchRegex(@"^\.nomercy-render-[0-9A-Z]{26}-0\.opus$");
    }

    [Fact]
    public async Task Render_ThreeInputsWithWindows_TwoOutputs_PutsEachWindowBeforeItsOwnInput()
    {
        WriteDataFile("downloads/voice.wav", 64);

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [
                        new(
                            PluginAudioInput.Track(_trackA.ToString()),
                            TimeSpan.FromSeconds(30),
                            TimeSpan.FromSeconds(15)
                        ),
                        new(
                            PluginAudioInput.Track(_trackB.ToString()),
                            Start: TimeSpan.FromSeconds(1.5)
                        ),
                        new(
                            PluginAudioInput.PluginFile("downloads/voice.wav"),
                            Duration: TimeSpan.FromSeconds(10)
                        ),
                    ],
                    "[0:a][1:a]acrossfade=d=5[mix];[2:a]anull[dry]",
                    [
                        new("mix", "out/mix.flac", PluginAudioCodec.Flac, SampleRate: 44100),
                        new("dry", "out/dry.wav", PluginAudioCodec.Wav),
                    ]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().BeNull();

        string[] arguments = _runs.Should().ContainSingle().Subject;
        IReadOnlyList<string> temporaries = arguments.Where(IsTemporaryOutput).ToList();
        temporaries.Should().HaveCount(2);
        Path.GetFileName(temporaries[0]).Should().EndWith("-0.flac");
        Path.GetFileName(temporaries[1]).Should().EndWith("-1.wav");

        arguments
            .Should()
            .Equal(
                "-nostdin",
                "-nostats",
                "-progress",
                "pipe:1",
                "-stats_period",
                "5",
                "-ss",
                "30",
                "-t",
                "15",
                "-i",
                LibraryPath("/track-a.flac"),
                "-ss",
                "1.5",
                "-i",
                LibraryPath("/track-b.flac"),
                "-t",
                "10",
                "-i",
                InDataFolder("downloads/voice.wav"),
                "-vn",
                "-sn",
                "-dn",
                "-filter_complex",
                "[0:a][1:a]acrossfade=d=5[mix];[2:a]anull[dry]",
                "-map",
                "[mix]",
                "-c:a",
                "flac",
                "-ar",
                "44100",
                temporaries[0],
                "-map",
                "[dry]",
                "-c:a",
                "pcm_s16le",
                temporaries[1]
            );
    }

    [Theory]
    [InlineData(PluginAudioCodec.Opus, 128, 24000, "libopus", ".opus")]
    [InlineData(PluginAudioCodec.Opus, null, null, "libopus", ".opus")]
    [InlineData(PluginAudioCodec.Flac, null, 96000, "flac", ".flac")]
    [InlineData(PluginAudioCodec.Wav, null, null, "pcm_s16le", ".wav")]
    public void RenderArguments_PerCodec(
        PluginAudioCodec codec,
        int? bitrateKbps,
        int? sampleRate,
        string encoder,
        string extension
    )
    {
        PluginAudioRenderPlan.Extension(codec).Should().Be(extension);

        string[] arguments = PluginAudioArguments.Render(
            [new("in.flac", null, null)],
            "[0:a]anull[a]",
            [new("a", "a" + extension, "final", "temporary", codec, bitrateKbps, sampleRate)]
        );

        List<string> expected = ["-map", "[a]", "-c:a", encoder];
        if (bitrateKbps is not null)
            expected.AddRange(["-b:a", $"{bitrateKbps}k"]);
        if (sampleRate is not null)
            expected.AddRange(["-ar", $"{sampleRate}"]);
        expected.Add("temporary");

        arguments.Skip(Array.IndexOf(arguments, "-map")).Should().Equal(expected);
    }

    [Fact]
    public async Task Render_ReplacesTheGraphTokens_AndRunsInTheFfmpegFolder()
    {
        WriteDataFile("prompt.txt", 8);

        await CreateTools()
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]whisper=model={whisperModel}:prompt_file={pluginFile:prompt.txt}[out]",
                    [new("out", "out.wav", PluginAudioCodec.Wav)]
                ),
                null,
                null,
                DefaultOptions
            );

        string[] arguments = _runs.Should().ContainSingle().Subject;
        string graph = arguments[Array.IndexOf(arguments, "-filter_complex") + 1];

        graph.Should().NotContain("{whisperModel}").And.NotContain("{pluginFile:");
        graph.Should().Contain(PluginGraphTokens.EscapeForFilterGraph(InDataFolder("prompt.txt")));
    }

    // --- what is refused before ffmpeg starts -----------------------------

    [Theory]
    [InlineData("../escape.wav", "plugin file '../escape.wav' leaves the plugin's data folder")]
    [InlineData(
        "stems/../../escape.wav",
        "plugin file 'stems/../../escape.wav' leaves the plugin's data folder"
    )]
    [InlineData(
        "/etc/escape.wav",
        "plugin file '/etc/escape.wav' is an absolute path; only a path inside the plugin's data folder is allowed"
    )]
    [InlineData(
        "C:/escape.wav",
        "plugin file 'C:/escape.wav' is an absolute path; only a path inside the plugin's data folder is allowed"
    )]
    [InlineData("", "an output path names no file")]
    public async Task Render_RefusesAnOutputPathOutsideTheDataFolder_BeforeFfmpegStarts(
        string relativePath,
        string refusal
    )
    {
        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput(relativePath), null, null, DefaultOptions);

        result.Refusal.Should().Be(refusal);
        result.ExitCode.Should().Be(PluginAudioRunResult.RefusedExitCode);
        _runs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
    }

    [Theory]
    [InlineData("../secret.wav", "plugin file '../secret.wav' leaves the plugin's data folder")]
    [InlineData(
        "/etc/secret.wav",
        "plugin file '/etc/secret.wav' is an absolute path; only a path inside the plugin's data folder is allowed"
    )]
    [InlineData("missing.wav", "plugin file 'missing.wav' does not exist")]
    [InlineData("", "a plugin file input names no file")]
    public async Task Render_RefusesAPluginFileInputOutsideTheDataFolder_BeforeFfmpegStarts(
        string relativePath,
        string refusal
    )
    {
        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA(), new(PluginAudioInput.PluginFile(relativePath))],
                    "[0:a][1:a]amix=inputs=2[out]",
                    [new("out", "stems/out.wav", PluginAudioCodec.Wav)]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().Be(refusal);
        _runs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
        FoldersInDataFolder().Should().BeEmpty("a refused run makes no folder either");
    }

    [Fact]
    public async Task Render_RefusesAnUnknownTrack_WithoutNamingAnyPath()
    {
        Guid unknown = Guid.NewGuid();

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA(), new(PluginAudioInput.Track(unknown.ToString()))],
                    "[0:a][1:a]amix=inputs=2[out]",
                    [new("out", "out.wav", PluginAudioCodec.Wav)]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().Be($"track {unknown} has no file");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Render_RefusesWhenFfmpegIsNotInstalled()
    {
        _storage.Setup(storage => storage.Exists(FfmpegBinary)).Returns(false);

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput(), null, null, DefaultOptions);

        result.Refusal.Should().Be("ffmpeg is not installed");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Render_RefusesAGraphThatNamesAModelThatIsNotInstalled()
    {
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: false, whisperModelPresent: false));

        PluginAudioTools tools = CreateTools();

        PluginAudioRunResult whisper = await tools.RenderAsync(
            new(
                [TrackA()],
                "[0:a]whisper=model={whisperModel}[out]",
                [new("out", "out.wav", PluginAudioCodec.Wav)]
            ),
            null,
            null,
            DefaultOptions
        );
        PluginAudioRunResult stems = await tools.RenderAsync(
            new(
                [TrackA()],
                "[0:a]stemsplit=model={stemsModel}[voc][acc]",
                [
                    new("voc", "voc.flac", PluginAudioCodec.Flac),
                    new("acc", "acc.flac", PluginAudioCodec.Flac),
                ]
            ),
            null,
            null,
            DefaultOptions
        );

        whisper.Refusal.Should().Be("the whisper model is not installed");
        stems.Refusal.Should().Be("the stemsplit model is not installed");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public async Task Render_RunsStemsplitOnAModelOfThePluginsOwn_WithoutTheServersModel()
    {
        // A render exists so a plugin can split with another model. Asking
        // for the server's own model first would refuse exactly that.
        _probe
            .Setup(probe => probe.GetCachedReport())
            .Returns(Report(stemsplitModelPresent: false, whisperModelPresent: true));
        WriteDataFile("models/roformer.gguf", 16);

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]stemsplit=model={pluginFile:models/roformer.gguf}[voc][acc]",
                    [
                        new("voc", "voc.flac", PluginAudioCodec.Flac),
                        new("acc", "acc.flac", PluginAudioCodec.Flac),
                    ]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().BeNull();
        _runs.Should().ContainSingle();
    }

    [Fact]
    public async Task Render_RefusesASecondRunWhileOneIsInProgress_WhicheverCallStartedIt()
    {
        TaskCompletionSource<ProcessResult> pending = new();
        _ffmpeg = call =>
        {
            call.WriteOutputs(100);
            return pending.Task;
        };

        PluginAudioTools tools = CreateTools();
        Task<PluginAudioRunResult> first = tools.RenderAsync(
            OneOutput("first.wav"),
            null,
            null,
            DefaultOptions
        );
        await WaitForAsync(() => _runs.Count > 0, "the first render to reach ffmpeg");

        PluginAudioRunResult secondRender = await tools.RenderAsync(
            OneOutput("second.wav"),
            null,
            null,
            DefaultOptions
        );
        PluginAudioRunResult filterGraph = await tools.RunFilterGraphAsync(
            PluginAudioInput.Track(_trackA.ToString()),
            new("volume=1", Complex: false),
            null,
            null
        );

        secondRender.Refusal.Should().Be("another ffmpeg run of this plugin is still in progress");
        filterGraph.Refusal.Should().Be("another ffmpeg run of this plugin is still in progress");
        _runs.Should().ContainSingle();

        pending.SetResult(Exited(0));
        (await first).Refusal.Should().BeNull();
        FilesInDataFolder().Should().Equal("first.wav");
    }

    // --- what is left afterwards ------------------------------------------

    [Fact]
    public async Task Render_ThatSucceeds_LeavesExactlyTheNamedFiles_AndReportsTheirSizes()
    {
        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]asplit[a][b]",
                    [
                        new("a", "stems/one/vocals.flac", PluginAudioCodec.Flac),
                        new("b", "preview.opus", PluginAudioCodec.Opus),
                    ]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().BeNull();
        result.Ran.Should().BeTrue();
        result.ExitCode.Should().Be(0);
        result
            .Outputs.Should()
            .Equal(
                new PluginAudioRenderedFile("stems/one/vocals.flac", 1000),
                new PluginAudioRenderedFile("preview.opus", 2000)
            );

        FilesInDataFolder().Should().Equal("preview.opus", "stems/one/vocals.flac");
        new FileInfo(InDataFolder("stems/one/vocals.flac")).Length.Should().Be(1000);
        new FileInfo(InDataFolder("preview.opus")).Length.Should().Be(2000);
    }

    [Fact]
    public async Task Render_ThatFails_LeavesNoFile_AndNoFolderItMade()
    {
        _ffmpeg = call =>
        {
            // Half a file is what a failing ffmpeg leaves: it opened the
            // output and wrote the header before the graph gave up.
            call.WriteOutputs(100);
            call.StdErr?.Invoke("Error while filtering: Invalid argument");
            return Task.FromResult(Exited(234));
        };

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("stems/deep/out.wav"), null, null, DefaultOptions);

        result.ExitCode.Should().Be(234);
        result.Refusal.Should().BeNull("the run did start; its exit code says what became of it");
        result.Outputs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
        FoldersInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_ThatTimesOut_LeavesNoFile()
    {
        _ffmpeg = call =>
        {
            call.WriteOutputs(100);
            return call.HangAsync();
        };

        PluginAudioRunResult result = await CreateTools(runTimeout: TimeSpan.FromMilliseconds(50))
            .RenderAsync(OneOutput("stems/out.wav"), null, null, DefaultOptions);

        result.ExitCode.Should().Be(PluginAudioRunResult.TimedOutExitCode);
        result.Refusal.Should().BeNull();
        result.Outputs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
        FoldersInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_ThatIsCancelled_LeavesNoFile_AndTheCancellationPropagates()
    {
        bool started = false;
        _ffmpeg = call =>
        {
            call.WriteOutputs(100);
            started = true;
            return call.HangAsync();
        };
        using CancellationTokenSource cancel = new();

        Task<PluginAudioRunResult> render = CreateTools()
            .RenderAsync(OneOutput("stems/out.wav"), null, null, DefaultOptions, cancel.Token);
        await WaitForAsync(() => started, "the render to reach ffmpeg");
        FilesInDataFolder().Should().ContainSingle("ffmpeg is writing its temporary file");

        await cancel.CancelAsync();

        Func<Task> act = () => render;
        await act.Should().ThrowAsync<OperationCanceledException>();
        FilesInDataFolder().Should().BeEmpty();
        FoldersInDataFolder().Should().BeEmpty();
    }

    /// <summary>
    /// What the real <see cref="ProcessRunner" /> does on its kill signal:
    /// the process is gone at once, and the run comes back as exit 0. Deaf
    /// to the cancellation token on purpose - on that alone the real runner
    /// lets ffmpeg write on for five seconds and returns before the process
    /// has let go of its files.
    /// </summary>
    private Func<FfmpegCall, Task<ProcessResult>> EndsOnlyOnTheKillSignal(Action<bool> killed) =>
        async call =>
        {
            call.WriteOutputs(100);

            try
            {
                // Ten seconds and not for ever, so a kill signal that never
                // comes fails the test instead of hanging it.
                await Task.Delay(TimeSpan.FromSeconds(10), call.Kill);
            }
            catch (OperationCanceledException)
            {
                // Killed, which the real runner reports as a normal return.
            }

            killed(call.Kill.IsCancellationRequested);
            return Exited(0);
        };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Render_ThatTheHostEnds_IsKilledAtOnce_AndNotMistakenForAFinishedRun(
        bool stalled
    )
    {
        bool killed = false;
        _ffmpeg = EndsOnlyOnTheKillSignal(value => killed = value);

        PluginAudioRunOptions options = stalled
            ? new(TimeSpan.FromSeconds(30)) { StallTimeout = TimeSpan.FromMilliseconds(100) }
            : new(TimeSpan.FromMilliseconds(100));

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("stems/out.wav"), null, null, options);

        killed.Should().BeTrue("the host ends a run through the runner's kill signal");
        result
            .ExitCode.Should()
            .Be(
                stalled
                    ? PluginAudioRunResult.StalledExitCode
                    : PluginAudioRunResult.TimedOutExitCode,
                "a killed process reports exit 0, which is not what became of this run"
            );
        result.Outputs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_ThatIsCancelled_IsKilledAtOnce_AndStillPropagatesTheCancellation()
    {
        bool killed = false;
        bool started = false;
        Func<FfmpegCall, Task<ProcessResult>> ffmpeg = EndsOnlyOnTheKillSignal(value =>
            killed = value
        );
        _ffmpeg = call =>
        {
            started = true;
            return ffmpeg(call);
        };
        using CancellationTokenSource cancel = new();

        Task<PluginAudioRunResult> render = CreateTools()
            .RenderAsync(OneOutput(), null, null, DefaultOptions, cancel.Token);
        await WaitForAsync(() => started, "the render to reach ffmpeg");
        await cancel.CancelAsync();

        Func<Task> act = () => render;
        await act.Should().ThrowAsync<OperationCanceledException>();
        killed.Should().BeTrue();
        FilesInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_ThatFinishedItsFiles_IsNotEndedByAClockThatRunsOutWhileItHangsAtExit()
    {
        // "progress=end" stops both clocks: the files are complete, and what
        // is left is the exit hang the grace period deals with.
        _ffmpeg = call =>
        {
            call.WriteOutputs(1000);
            call.StdOut?.Invoke("progress=end");
            return call.HangAsync();
        };

        PluginAudioRunResult result = await CreateTools(
                exitGracePeriod: TimeSpan.FromMilliseconds(600)
            )
            .RenderAsync(
                OneOutput(),
                null,
                null,
                new(TimeSpan.FromMilliseconds(150))
                {
                    StallTimeout = TimeSpan.FromMilliseconds(100),
                }
            );

        result.ExitCode.Should().Be(0);
        result.Outputs.Should().Equal(new PluginAudioRenderedFile("out.wav", 1000));
    }

    [Fact]
    public async Task Render_ThatFails_KeepsTheFileThatWasAlreadyThere()
    {
        WriteDataFile("mix.wav", 321);
        _ffmpeg = call =>
        {
            call.WriteOutputs(100);
            return Task.FromResult(Exited(1));
        };

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("mix.wav"), null, null, DefaultOptions);

        result.ExitCode.Should().Be(1);
        FilesInDataFolder().Should().Equal("mix.wav");
        File.ReadAllBytes(InDataFolder("mix.wav")).Should().OnlyContain(value => value == 7);
        new FileInfo(InDataFolder("mix.wav")).Length.Should().Be(321);
    }

    [Fact]
    public async Task Render_ThatSucceeds_ReplacesAFileThatWasAlreadyThere()
    {
        WriteDataFile("mix.wav", 321);

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("mix.wav"), null, null, DefaultOptions);

        result.Outputs.Should().Equal(new PluginAudioRenderedFile("mix.wav", 1000));
        FilesInDataFolder().Should().Equal("mix.wav");
        File.ReadAllBytes(InDataFolder("mix.wav")).Should().OnlyContain(value => value == 0);
    }

    [Fact]
    public async Task Render_ThatExitsZeroWithoutWritingAFile_IsRefused_AndLeavesNothing()
    {
        _ffmpeg = call =>
        {
            // Only the first output: a pad that produced no frames.
            File.WriteAllBytes(call.OutputPaths[0], new byte[10]);
            return Task.FromResult(Exited(0));
        };

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]asplit[a][b]",
                    [
                        new("a", "a.wav", PluginAudioCodec.Wav),
                        new("b", "b.wav", PluginAudioCodec.Wav),
                    ]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().Be("the render wrote no file for pad 'b'");
        FilesInDataFolder().Should().BeEmpty("one output of two is not a result");
    }

    [Fact]
    public async Task Render_RemovesWhatAnInterruptedRunLeftInTheFolder()
    {
        // A server that stopped mid-render never got to clean up.
        WriteDataFile(
            $"stems/{PluginAudioRenderPlan.TempPrefix}01HZZZZZZZZZZZZZZZZZZZZZZZ-0.flac",
            50
        );
        WriteDataFile("stems/keep.flac", 5);

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("stems/out.wav"), null, null, DefaultOptions);

        result.Refusal.Should().BeNull();
        FilesInDataFolder().Should().Equal("stems/keep.flac", "stems/out.wav");
    }

    [Fact]
    public async Task Render_EndsARunThatFinishedItsFilesButDidNotExit()
    {
        // The same libgomp exit hang the stem split guards against: the
        // outputs are complete when "progress=end" is printed.
        _ffmpeg = call =>
        {
            call.WriteOutputs(1000);
            call.StdOut?.Invoke("progress=end");
            return call.HangAsync();
        };

        PluginAudioRunResult result = await CreateTools(
                runTimeout: TimeSpan.FromSeconds(30),
                exitGracePeriod: TimeSpan.FromMilliseconds(20)
            )
            .RenderAsync(OneOutput(), null, null, DefaultOptions);

        result.Refusal.Should().BeNull();
        result.ExitCode.Should().Be(0);
        result.Outputs.Should().Equal(new PluginAudioRenderedFile("out.wav", 1000));
    }

    [Fact]
    public async Task Render_HandsEveryStdOutAndStdErrLineToThePlugin()
    {
        _ffmpeg = call =>
        {
            call.StdErr?.Invoke("ffmpeg version 9.0-NoMercy-MediaServer");
            call.StdOut?.Invoke("lavfi.r128.I=-14.2");
            call.StdOut?.Invoke("out_time_us=5000000");
            call.WriteOutputs(10);
            call.StdOut?.Invoke("progress=end");
            return Task.FromResult(Exited(0));
        };
        List<string> stdOut = [];
        List<string> stdErr = [];

        await CreateTools().RenderAsync(OneOutput(), stdOut.Add, stdErr.Add, DefaultOptions);

        stdOut.Should().Equal("lavfi.r128.I=-14.2", "out_time_us=5000000", "progress=end");
        stdErr.Should().Equal("ffmpeg version 9.0-NoMercy-MediaServer");
    }

    // --- the stall limit --------------------------------------------------

    [Fact]
    public async Task Render_WithAStallLimit_EndsARunThatPrintsNoProgress()
    {
        _ffmpeg = call =>
        {
            call.WriteOutputs(100);
            return call.HangAsync();
        };

        // The total is ten seconds only so that a stall limit that does not
        // work fails this test instead of hanging it.
        PluginAudioRunResult result = await CreateTools(runTimeout: TimeSpan.FromSeconds(10))
            .RenderAsync(
                OneOutput(),
                null,
                null,
                new(TimeSpan.FromSeconds(10)) { StallTimeout = TimeSpan.FromMilliseconds(100) }
            );

        result.ExitCode.Should().Be(PluginAudioRunResult.StalledExitCode);
        result.Refusal.Should().BeNull();
        FilesInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_WithAStallLimit_EndsARunThatKeepsReportingTheSamePosition()
    {
        // What ffmpeg does while its input stopped answering: the report
        // still arrives on its timer, and says what the last one said.
        _ffmpeg = async call =>
        {
            call.WriteOutputs(100);
            while (true)
            {
                call.StdOut?.Invoke("total_size=210184");
                call.StdOut?.Invoke("out_time_us=16927347");
                call.StdOut?.Invoke("progress=continue");
                await Task.Delay(20, call.Cancelled);
            }
        };

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                OneOutput(),
                null,
                null,
                new(TimeSpan.FromSeconds(10)) { StallTimeout = TimeSpan.FromMilliseconds(300) }
            );

        result.ExitCode.Should().Be(PluginAudioRunResult.StalledExitCode);
        FilesInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_WithAStallLimit_DoesNotEndARunThatKeepsMoving_PastItsTotalAndItsStallLimit()
    {
        // Two and a half seconds of steady progress: more than twice the
        // stall limit, and many times the total a run without one would get.
        _ffmpeg = async call =>
        {
            for (int report = 1; report <= 50; report++)
            {
                call.StdOut?.Invoke($"out_time_us={report * 5_000_000L}");
                call.StdOut?.Invoke("progress=continue");
                await Task.Delay(50, call.Cancelled);
            }

            call.WriteOutputs(1000);
            call.StdOut?.Invoke("progress=end");
            return Exited(0);
        };

        PluginAudioRunResult result = await CreateTools(runTimeout: TimeSpan.FromMilliseconds(200))
            .RenderAsync(
                OneOutput(),
                null,
                null,
                new(null) { StallTimeout = TimeSpan.FromSeconds(1) }
            );

        result.ExitCode.Should().Be(0);
        result.Outputs.Should().Equal(new PluginAudioRenderedFile("out.wav", 1000));
    }

    private static readonly TimeSpan Shortest = PluginAudioRunLimits.ShortestStall;

    [Fact]
    public void RunLimits_WithoutAStallLimit_AreTheTotalInsideTheMaximum()
    {
        TimeSpan fallback = TimeSpan.FromMinutes(10);

        PluginAudioRunLimits
            .ForRender(null, fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(fallback, null));
        PluginAudioRunLimits
            .ForRender(new(TimeSpan.FromMinutes(30)), fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(TimeSpan.FromMinutes(30), null));
        PluginAudioRunLimits
            .ForRender(new(TimeSpan.FromMinutes(90)), fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(TimeSpan.FromMinutes(60), null));
        PluginAudioRunLimits
            .ForRender(new(null) { StallTimeout = TimeSpan.Zero }, fallback, Shortest)
            .Should()
            .Be(
                new PluginAudioRunLimits(fallback, null),
                "a stall limit of nothing is no stall limit"
            );
    }

    [Fact]
    public void RunLimits_WithAStallLimit_LetTheTotalRunPastSixtyMinutes()
    {
        TimeSpan fallback = TimeSpan.FromMinutes(10);
        TimeSpan stall = TimeSpan.FromMinutes(5);

        // No total asked for: judged by progress alone.
        PluginAudioRunLimits
            .ForRender(new(null) { StallTimeout = stall }, fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(Timeout.InfiniteTimeSpan, stall));

        // A total asked for: kept as asked, not clamped to sixty minutes.
        PluginAudioRunLimits
            .ForRender(new(TimeSpan.FromHours(6)) { StallTimeout = stall }, fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(TimeSpan.FromHours(6), stall));

        // The stall limit itself stays inside the maximum.
        PluginAudioRunLimits
            .ForRender(new(null) { StallTimeout = TimeSpan.FromHours(3) }, fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(Timeout.InfiniteTimeSpan, TimeSpan.FromMinutes(60)));

        // A total no timer can hold is no total.
        PluginAudioRunLimits
            .ForRender(new(TimeSpan.MaxValue) { StallTimeout = stall }, fallback, Shortest)
            .Should()
            .Be(new PluginAudioRunLimits(Timeout.InfiniteTimeSpan, stall));
    }

    [Fact]
    public void RunLimits_RaiseAStallLimitThatIsShorterThanFfmpegReports()
    {
        // ffmpeg reports every five seconds. A limit under that would end
        // every run before the report that shows it moving could arrive.
        TimeSpan fallback = TimeSpan.FromMinutes(10);

        Shortest.Should().Be(TimeSpan.FromSeconds(10));
        PluginAudioRunLimits
            .ForRender(new(null) { StallTimeout = TimeSpan.FromSeconds(2) }, fallback, Shortest)
            .Stall.Should()
            .Be(TimeSpan.FromSeconds(10));
        PluginAudioRunLimits
            .ForRender(new(null) { StallTimeout = TimeSpan.FromSeconds(45) }, fallback, Shortest)
            .Stall.Should()
            .Be(TimeSpan.FromSeconds(45));
    }

    [Fact]
    public async Task RunFilterGraph_IgnoresAStallLimit_BecauseItReportsNoProgress()
    {
        _ffmpeg = async call =>
        {
            await Task.Delay(300, call.Cancelled);
            return Exited(0);
        };

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.Track(_trackA.ToString()),
                new("volume=1", Complex: false),
                null,
                null,
                new PluginAudioRunOptions(null) { StallTimeout = TimeSpan.FromMilliseconds(50) }
            );

        result.ExitCode.Should().Be(0);
    }

    // --- the disk quota ---------------------------------------------------

    [Fact]
    public async Task Render_CountsTheFilesItKeepsAgainstTheQuota_AndGivesBackWhatItReplaces()
    {
        PluginQuotaMeter meter = Meter(diskBytes: 100_000);
        PluginAudioTools tools = CreateTools(quotas: meter);

        await tools.RenderAsync(
            new(
                [TrackA()],
                "[0:a]asplit[a][b]",
                [new("a", "a.wav", PluginAudioCodec.Wav), new("b", "b.wav", PluginAudioCodec.Wav)]
            ),
            null,
            null,
            DefaultOptions
        );
        meter.DiskUsed(_pluginId).Should().Be(3000);

        // The same two files again, each a quarter of the size.
        _ffmpeg = call =>
        {
            call.WriteOutputs(250);
            return Task.FromResult(Exited(0));
        };
        await tools.RenderAsync(
            new(
                [TrackA()],
                "[0:a]asplit[a][b]",
                [new("a", "a.wav", PluginAudioCodec.Wav), new("b", "b.wav", PluginAudioCodec.Wav)]
            ),
            null,
            null,
            DefaultOptions
        );

        meter.DiskUsed(_pluginId).Should().Be(750);
    }

    [Fact]
    public async Task Render_RefusesOutputsThatDoNotFitTheQuota_AndKeepsNone()
    {
        PluginQuotaMeter meter = Meter(diskBytes: 1500);
        WriteDataFile("a.wav", 40);

        PluginAudioRunResult result = await CreateTools(quotas: meter)
            .RenderAsync(
                new(
                    [TrackA()],
                    "[0:a]asplit[a][b]",
                    [
                        new("a", "a.wav", PluginAudioCodec.Wav),
                        new("b", "b.wav", PluginAudioCodec.Wav),
                    ]
                ),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().StartWith("the render does not fit the plugin's disk quota.");
        result.Refusal.Should().Contain("Raise the plugin's disk allowance");
        result.Outputs.Should().BeEmpty();
        meter.DiskUsed(_pluginId).Should().Be(0);
        FilesInDataFolder().Should().Equal("a.wav");
        new FileInfo(InDataFolder("a.wav")).Length.Should().Be(40, "the file that was there stays");
    }

    [Fact]
    public async Task Render_EndsARunThatOutgrowsTheQuotaWhileItIsStillWriting()
    {
        // A generator has no input to wait for: it would fill the disk long
        // before a check made after the run got its turn.
        long written = 0;
        _ffmpeg = async call =>
        {
            await using FileStream output = new(
                call.OutputPaths[0],
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite | FileShare.Delete
            );
            while (true)
            {
                await output.WriteAsync(new byte[4096], call.Cancelled);
                await output.FlushAsync(call.Cancelled);
                written += 4096;
                await Task.Delay(5, call.Cancelled);
            }
        };
        PluginQuotaMeter meter = Meter(diskBytes: 20_000);

        PluginAudioRunResult result = await CreateTools(
                runTimeout: TimeSpan.FromSeconds(10),
                quotaCheckInterval: TimeSpan.FromMilliseconds(20),
                quotas: meter
            )
            .RenderAsync(OneOutput(), null, null, DefaultOptions);

        result.Refusal.Should().StartWith("the render does not fit the plugin's disk quota.");
        written.Should().BeGreaterThan(20_000, "the run was ended because of what it wrote");
        meter.DiskUsed(_pluginId).Should().Be(0);
        FilesInDataFolder().Should().BeEmpty();
    }

    // --- the plan ---------------------------------------------------------

    public static TheoryData<PluginAudioRender, string> RefusedRenders()
    {
        PluginAudioSource input = new(PluginAudioInput.Track(Guid.Empty.ToString()));
        PluginAudioOutput output = new("out", "out.wav", PluginAudioCodec.Wav);
        const string graph = "[0:a]anull[out]";

        return new()
        {
            { new([], graph, [output]), "a render needs at least one input" },
            {
                new(Enumerable.Repeat(input, 9).ToList(), graph, [output]),
                "a render reads at most 8 inputs; this one names 9"
            },
            {
                new([new(input.Input, TimeSpan.FromSeconds(-1))], graph, [output]),
                "input 0 starts before the beginning of its file"
            },
            {
                new([input, new(input.Input, Duration: TimeSpan.Zero)], graph, [output]),
                "input 1 has a duration that is not above zero"
            },
            { new([input], "  ", [output]), "the graph is empty" },
            { new([input], graph, []), "a render needs at least one output" },
            {
                new(
                    [input],
                    graph,
                    Enumerable
                        .Range(0, 9)
                        .Select(index => new PluginAudioOutput(
                            $"p{index}",
                            $"{index}.wav",
                            PluginAudioCodec.Wav
                        ))
                        .ToList()
                ),
                "a render writes at most 8 outputs; this one names 9"
            },
            {
                new([input], graph, [new("", "out.wav", PluginAudioCodec.Wav)]),
                "output 'out.wav' names no pad"
            },
            {
                new([input], graph, [new("[out]", "out.wav", PluginAudioCodec.Wav)]),
                "pad '[out]' is written without brackets"
            },
            {
                new(
                    [input],
                    graph,
                    [
                        new("out", "a.wav", PluginAudioCodec.Wav),
                        new("out", "b.wav", PluginAudioCodec.Wav),
                    ]
                ),
                "pad 'out' feeds more than one output"
            },
            {
                new(
                    [input],
                    graph,
                    [
                        new("a", "stems/Mix.wav", PluginAudioCodec.Wav),
                        new("b", "stems/mix.wav", PluginAudioCodec.Wav),
                    ]
                ),
                "output 'stems/mix.wav' is named more than once"
            },
            {
                new([input], graph, [new("out", "out.flac", PluginAudioCodec.Flac, 320)]),
                "output 'out.flac' sets a bitrate, which only Opus has"
            },
            {
                new([input], graph, [new("out", "out.opus", PluginAudioCodec.Opus, 2)]),
                "output 'out.opus' asks for 2 kbit/s; Opus takes 6 to 510"
            },
            {
                new(
                    [input],
                    graph,
                    [new("out", "out.opus", PluginAudioCodec.Opus, SampleRate: 44100)]
                ),
                "output 'out.opus' asks for 44100 Hz; Opus encodes at 8000, 12000, 16000, 24000, 48000 Hz only"
            },
            {
                new([input], graph, [new("out", "out.wav", PluginAudioCodec.Wav, SampleRate: 0)]),
                "output 'out.wav' asks for a sample rate of 0 Hz"
            },
            {
                new([input], graph, [new("out", "out.bin", (PluginAudioCodec)42)]),
                "output 'out.bin' asks for a codec this host does not encode"
            },
        };
    }

    [Theory]
    [MemberData(nameof(RefusedRenders))]
    public async Task Render_RefusesARequestItCanSeeIsWrong_BeforeFfmpegStarts(
        PluginAudioRender render,
        string refusal
    )
    {
        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(render, null, null, DefaultOptions);

        result.Refusal.Should().Be(refusal);
        _runs.Should().BeEmpty();
        FilesInDataFolder().Should().BeEmpty();
    }

    [Fact]
    public async Task Render_RefusesAnOutputThatIsAFolder()
    {
        Directory.CreateDirectory(InDataFolder("stems"));

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(OneOutput("stems"), null, null, DefaultOptions);

        result.Refusal.Should().Be("output 'stems' is a folder");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public void Plan_RefusesAnOutputReachedThroughALink()
    {
        // A link inside the folder can point anywhere; the path check alone
        // cannot see where, and ffmpeg would follow it.
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.ResolveLinkTarget(Path.Combine(Path.GetFullPath(_dataFolder), "linked")))
            .Returns("/somewhere/else");

        PluginAudioRenderPlan plan = PluginAudioRenderPlan.Build(
            OneOutput("linked/out.wav"),
            _dataFolder,
            driver.Object
        );

        plan.Refusal.Should().Be("plugin file 'linked/out.wav' leaves the plugin's data folder");
        plan.Targets.Should().BeEmpty();
    }

    [Fact]
    public void Plan_WithoutADataFolder_RefusesEveryOutput()
    {
        PluginAudioRenderPlan plan = PluginAudioRenderPlan.Build(OneOutput(), null, _driver);

        plan.Refusal.Should()
            .Be("an output path needs the plugin's data folder, which this host did not provide");
    }

    // --- putting the files in place ---------------------------------------

    private PluginAudioRenderTarget Target(string relativePath, int index) =>
        new(
            $"p{index}",
            relativePath,
            InDataFolder(relativePath),
            InDataFolder($"{PluginAudioRenderPlan.TempPrefix}BATCH-{index}.wav"),
            PluginAudioCodec.Wav,
            null,
            null
        );

    private PluginAudioRenderFiles Files(params PluginAudioRenderTarget[] targets) =>
        new(_pluginId, _dataFolder, targets, _driver, NullLogger.Instance);

    [Fact]
    public void Commit_ThatCannotMoveAFileAside_ChangesNothing()
    {
        // Two files to replace. The second cannot be moved aside, the way a
        // file the plugin still holds open cannot be on Windows.
        PluginAudioRenderTarget first = Target("a.wav", 0);
        PluginAudioRenderTarget second = Target("b.wav", 1);
        WriteDataFile("a.wav", 11);
        WriteDataFile("b.wav", 22);

        PluginAudioRenderFiles files = Files(first, second);
        files.Prepare();
        File.WriteAllBytes(first.TempPath, new byte[100]);
        File.WriteAllBytes(second.TempPath, new byte[200]);
        Directory.CreateDirectory(second.TempPath + ".previous");

        Action commit = files.Commit;
        commit.Should().Throw<IOException>();
        files.Cleanup();
        Directory.Delete(second.TempPath + ".previous");

        // Neither new file landed, and the first old file is back in place.
        FilesInDataFolder().Should().Equal("a.wav", "b.wav");
        new FileInfo(InDataFolder("a.wav")).Length.Should().Be(11);
        new FileInfo(InDataFolder("b.wav")).Length.Should().Be(22);
    }

    [Fact]
    public void Commit_PlacesEveryFile_AndLeavesNothingUnderATemporaryName()
    {
        PluginAudioRenderTarget first = Target("a.wav", 0);
        PluginAudioRenderTarget second = Target("b.wav", 1);
        WriteDataFile("a.wav", 11);

        PluginAudioRenderFiles files = Files(first, second);
        files.Prepare();
        files.ReplacedBytes.Should().Be(11);

        File.WriteAllBytes(first.TempPath, new byte[100]);
        File.WriteAllBytes(second.TempPath, new byte[200]);
        files.NetBytes().Should().Be(289);

        files.Commit();
        files.Cleanup();

        FilesInDataFolder().Should().Equal("a.wav", "b.wav");
        files
            .Rendered()
            .Should()
            .Equal(
                new PluginAudioRenderedFile("a.wav", 100),
                new PluginAudioRenderedFile("b.wav", 200)
            );
    }

    // --- the contract -----------------------------------------------------

    [Fact]
    public async Task RunFilterGraph_ReadsAFileFromThePluginsOwnFolder()
    {
        WriteDataFile("downloads/clip.wav", 64);
        _ffmpeg = _ => Task.FromResult(Exited(0));

        PluginAudioRunResult result = await CreateTools()
            .RunFilterGraphAsync(
                PluginAudioInput.PluginFile("downloads/clip.wav"),
                new("ebur128", Complex: false),
                null,
                null
            );

        result.Refusal.Should().BeNull();
        result.Outputs.Should().BeEmpty("a filter-graph run writes no file");
        string[] arguments = _runs.Should().ContainSingle().Subject;
        arguments[Array.IndexOf(arguments, "-i") + 1]
            .Should()
            .Be(InDataFolder("downloads/clip.wav"));
    }

    [Fact]
    public void AnInput_NamesExactlyOneSource()
    {
        PluginAudioInput file = PluginAudioInput.PluginFile("downloads/clip.wav");

        file.PluginFilePath.Should().Be("downloads/clip.wav");
        file.TrackId.Should().BeNull();
        file.StorageKey.Should().BeNull();
        PluginAudioInput.Track("t").PluginFilePath.Should().BeNull();
        PluginAudioInput.Derived("k").PluginFilePath.Should().BeNull();
    }

    [Fact]
    public async Task AnInputThatNamesTwoSources_IsRefused()
    {
        PluginAudioInput both = PluginAudioInput.Track(_trackA.ToString()) with
        {
            PluginFilePath = "clip.wav",
        };

        PluginAudioRunResult result = await CreateTools()
            .RenderAsync(
                new([new(both)], "[0:a]anull[out]", [new("out", "out.wav", PluginAudioCodec.Wav)]),
                null,
                null,
                DefaultOptions
            );

        result.Refusal.Should().Be("the input names more than one source");
        _runs.Should().BeEmpty();
    }

    [Fact]
    public void ARunResult_HasNoOutputsUnlessARenderKeptSome_AndStillComparesByValue()
    {
        new PluginAudioRunResult(0, null).Outputs.Should().BeEmpty();
        PluginAudioRunResult.Refused("no").Outputs.Should().BeEmpty();
        new PluginAudioRunResult(0, null).Should().Be(new PluginAudioRunResult(0, null));
        PluginAudioRunResult.Refused("no").ExitCode.Should().Be(-1);
    }

    [Fact]
    public async Task AnImplementerThatPredatesRender_RefusesIt()
    {
        IPluginAudioTools older = new AudioToolsBeforeRender();

        PluginAudioRunResult result = await older.RenderAsync(
            OneOutput(),
            null,
            null,
            DefaultOptions
        );

        result.Refusal.Should().Be("this host cannot render audio to a file");
        result.Ran.Should().BeFalse();
    }

    /// <summary>What a host or a test double written against ABI 12.6 implements: nothing newer.</summary>
    private sealed class AudioToolsBeforeRender : IPluginAudioTools
    {
        public Task<PluginAudioRunResult> RunFilterGraphAsync(
            PluginAudioInput input,
            PluginFilterGraph graph,
            Action<string>? onStdOut,
            Action<string>? onStdErr,
            CancellationToken ct = default
        ) => Task.FromResult(new PluginAudioRunResult(0, null));

        public Task<PluginStemSplitResult> SplitStemsAsync(
            string trackId,
            PluginStemCoverage coverage,
            PluginStemSet stemSet,
            CancellationToken ct = default
        ) => Task.FromResult(PluginStemSplitResult.Refused("not here"));
    }
}
