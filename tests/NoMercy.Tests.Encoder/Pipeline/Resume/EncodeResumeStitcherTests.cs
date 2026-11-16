using System.Globalization;
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Pipeline.Resume;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// A resumed run writes a playlist of its own segments only. The stitch puts the
/// entries of the segments before the resume point back in front of them.
/// </summary>
public class EncodeResumeStitcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"resume_stitch_{Guid.NewGuid():N}"
    );
    private readonly IStorage _storage = TestStorageFactory.CreateLocal();
    private readonly FakeProber _prober = new();
    private readonly CapturingLogger _logger = new();
    private readonly EncodeResumeStitcher _stitcher;

    public EncodeResumeStitcherTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "video"));
        Directory.CreateDirectory(Path.Combine(_dir, "audio"));
        _stitcher = new(_storage, _prober, _logger);
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task Stitch_PutsTheRebuiltEntriesBeforeFfmpegsAndKeepsTheOtherTags()
    {
        WritePlaylist("video/v.m3u8", Playlist("v", ".ts", 2, [2.002, 1.001], mapTag: false));
        _prober.Set("v_00000.ts", 1.4);
        _prober.Set("v_00001.ts", 3.402);
        _prober.Set("v_00002.ts", 5.404);

        string? error = await _stitcher.StitchAsync(
            [MainCommand(2, false)],
            _dir,
            "run-1",
            default
        );

        error.Should().BeNull();
        ReadPlaylist("video/v.m3u8")
            .Should()
            .Be(
                "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:2\n#EXT-X-MEDIA-SEQUENCE:0\n"
                    + "#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n"
                    + "#EXTINF:2.002000,\nv_00000.ts\n#EXTINF:2.002000,\nv_00001.ts\n"
                    + "#EXTINF:2.002000,\nv_00002.ts\n#EXTINF:1.001000,\nv_00003.ts\n"
                    + "#EXT-X-ENDLIST\n"
            );
    }

    [Fact]
    public async Task Stitch_TargetDurationIsTheRoundedLongestEntry()
    {
        WritePlaylist("video/v.m3u8", Playlist("v", ".ts", 1, [2.002], mapTag: false));
        _prober.Set("v_00000.ts", 0);
        _prober.Set("v_00001.ts", 4.4);

        await _stitcher.StitchAsync([MainCommand(1, false)], _dir, "run-1", default);

        ReadPlaylist("video/v.m3u8")
            .Should()
            .Contain("#EXT-X-TARGETDURATION:4\n")
            .And.Contain("#EXTINF:4.400000,\nv_00000.ts\n");
    }

    [Fact]
    public async Task Stitch_Fmp4_KeepsTheMapTag()
    {
        WritePlaylist("video/v.m3u8", Playlist("v", ".m4s", 1, [2.002], mapTag: true));
        _prober.Set("v_00000.m4s", 0);
        _prober.Set("v_00001.m4s", 2.002);

        string? error = await _stitcher.StitchAsync([MainCommand(1, true)], _dir, "run-1", default);

        error.Should().BeNull();
        ReadPlaylist("video/v.m3u8")
            .Should()
            .Contain("#EXT-X-MAP:URI=\"init.mp4\"\n")
            .And.Contain("#EXTINF:2.002000,\nv_00000.m4s\n#EXTINF:2.002000,\nv_00001.m4s\n")
            .And.Contain("#EXT-X-MEDIA-SEQUENCE:0\n");
    }

    [Fact]
    public async Task Stitch_ProbeFails_LeavesThePlaylistAndLogsOneLine()
    {
        string original = Playlist("v", ".ts", 2, [2.002], mapTag: false);
        WritePlaylist("video/v.m3u8", original);
        _prober.Set("v_00000.ts", 1.4);

        string? error = await _stitcher.StitchAsync(
            [MainCommand(2, false)],
            _dir,
            "run-1",
            default
        );

        error.Should().NotBeNull();
        ReadPlaylist("video/v.m3u8").Should().Be(original);
        _logger.Lines.Should().ContainSingle().Which.Should().Contain("v_00001.ts");
    }

    [Fact]
    public async Task Stitch_SecondOutputFails_LeavesEveryPlaylistAsItWas()
    {
        string video = Playlist("v", ".ts", 1, [2.002], mapTag: false);
        string audio = Playlist("a", ".ts", 1, [2.005], mapTag: false);
        WritePlaylist("video/v.m3u8", video);
        WritePlaylist("audio/a.m3u8", audio);
        _prober.Set("v_00000.ts", 0);
        _prober.Set("v_00001.ts", 2.002);

        string? error = await _stitcher.StitchAsync(
            [MainCommand(1, false, audio: true)],
            _dir,
            "run-1",
            default
        );

        error.Should().NotBeNull();
        ReadPlaylist("video/v.m3u8").Should().Be(video);
        ReadPlaylist("audio/a.m3u8").Should().Be(audio);
    }

    [Fact]
    public async Task Stitch_BothOutputs_AreStitched()
    {
        WritePlaylist("video/v.m3u8", Playlist("v", ".ts", 1, [2.002], mapTag: false));
        WritePlaylist("audio/a.m3u8", Playlist("a", ".ts", 1, [2.005], mapTag: false));
        _prober.Set("v_00000.ts", 0);
        _prober.Set("v_00001.ts", 2.002);
        _prober.Set("a_00000.ts", 0.013);
        _prober.Set("a_00001.ts", 2.0);

        string? error = await _stitcher.StitchAsync(
            [MainCommand(1, false, audio: true)],
            _dir,
            "run-1",
            default
        );

        error.Should().BeNull();
        ReadPlaylist("video/v.m3u8").Should().Contain("#EXTINF:2.002000,\nv_00000.ts\n");
        ReadPlaylist("audio/a.m3u8").Should().Contain("#EXTINF:1.987000,\na_00000.ts\n");
        Directory.GetFiles(_dir, "*.stitch", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Fact]
    public async Task Stitch_CommandWithoutAResumeMarker_ChangesNothing()
    {
        string original = Playlist("v", ".ts", 2, [2.002], mapTag: false);
        WritePlaylist("video/v.m3u8", original);

        string? error = await _stitcher.StitchAsync(
            [MainCommand(2, false) with { ResumeMarkerPath = null }],
            _dir,
            "run-1",
            default
        );

        error.Should().BeNull();
        _prober.Calls.Should().BeEmpty();
        ReadPlaylist("video/v.m3u8").Should().Be(original);
    }

    private static FfmpegCommand MainCommand(int start, bool fmp4, bool audio = false)
    {
        List<string> args =
        [
            "-i",
            "in.mkv",
            "-start_number",
            start.ToString(),
            "-f",
            "hls",
            "-hls_segment_filename",
            fmp4 ? "video/v_%05d.m4s" : "video/v_%05d.ts",
            "video/v.m3u8",
        ];
        if (audio)
            args.AddRange([
                "-f",
                "hls",
                "-hls_segment_filename",
                "audio/a_%05d.ts",
                "audio/a.m3u8",
            ]);

        return new("ffmpeg", [.. args], null, ResumeMarkerPath: "marker");
    }

    private static string Playlist(
        string name,
        string extension,
        int start,
        double[] durations,
        bool mapTag
    )
    {
        string text =
            "#EXTM3U\n#EXT-X-VERSION:6\n#EXT-X-TARGETDURATION:2\n"
            + $"#EXT-X-MEDIA-SEQUENCE:{start}\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXT-X-INDEPENDENT-SEGMENTS\n";
        if (mapTag)
            text += "#EXT-X-MAP:URI=\"init.mp4\"\n";

        for (int i = 0; i < durations.Length; i++)
            text +=
                $"#EXTINF:{durations[i].ToString("F6", CultureInfo.InvariantCulture)},\n"
                + $"{name}_{start + i:D5}{extension}\n";

        return text + "#EXT-X-ENDLIST\n";
    }

    private void WritePlaylist(string relative, string text) =>
        File.WriteAllText(Path.Combine(_dir, relative), text);

    private string ReadPlaylist(string relative) => File.ReadAllText(Path.Combine(_dir, relative));

    private sealed class FakeProber : ISegmentProber
    {
        private readonly Dictionary<string, double> _pts = [];
        public List<string> Calls { get; } = [];

        public void Set(string fileName, double seconds) => _pts[fileName] = seconds;

        public Task<double?> FirstPtsAsync(string segmentPath, CancellationToken ct)
        {
            string name = Path.GetFileName(segmentPath);
            lock (Calls)
                Calls.Add(name);
            return Task.FromResult(_pts.TryGetValue(name, out double v) ? v : (double?)null);
        }

        public Task<double?> FirstPacketPtsAsync(
            string segmentPath,
            bool video,
            CancellationToken ct
        ) => FirstPtsAsync(segmentPath, ct);
    }

    private sealed class CapturingLogger : ILogger<EncodeResumeStitcher>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) => Lines.Add(formatter(state, exception));
    }
}
