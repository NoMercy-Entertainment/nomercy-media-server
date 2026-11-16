using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Resume;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.Profiles;
using NoMercy.Storage;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Resume;

/// <summary>
/// The encode result is the job's verdict: a resumed run whose playlists could not
/// be stitched fails, so the bundle is never published half-stitched.
/// </summary>
public class EncoderResumeStitchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(),
        $"resume_encoder_{Guid.NewGuid():N}"
    );
    private readonly IStorage _storage = TestStorageFactory.CreateLocal();
    private readonly Mock<IFinalizeStage> _finalize = new();

    public EncoderResumeStitchTests()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "video"));
        File.WriteAllText(
            Path.Combine(_dir, "video", "v.m3u8"),
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:1\n#EXTINF:2.000000,\nv_00001.ts\n#EXT-X-ENDLIST\n"
        );
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task ResumedRun_StitchFails_ReportsFailureAndDoesNotFinalize()
    {
        EncodingResult result = await Encode(new SegmentProber(null));

        result.Success.Should().BeFalse();
        result.Error!.Message.Should().Contain("could not be stitched");
        _finalize.Verify(
            f =>
                f.ExecuteAsync(
                    It.IsAny<FinalizeInput>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task ResumedRun_StitchSucceeds_Finalizes()
    {
        EncodingResult result = await Encode(new SegmentProber(1.0));

        result.Success.Should().BeTrue(result.Error?.Message);
        File.ReadAllText(Path.Combine(_dir, "video", "v.m3u8"))
            .Should()
            .Contain("#EXT-X-MEDIA-SEQUENCE:0\n")
            .And.Contain("v_00000.ts");
    }

    private async Task<EncodingResult> Encode(ISegmentProber prober)
    {
        FfmpegCommand[] commands =
        [
            new(
                "ffmpeg",
                ["-start_number", "1", "-hls_segment_filename", "video/v_%05d.ts", "video/v.m3u8"],
                null,
                ResumeMarkerPath: "marker"
            ),
        ];
        MediaInfo info = new(
            FilePath: "/media/in.mkv",
            Format: "matroska",
            Duration: TimeSpan.FromSeconds(10),
            OverallBitRateKbps: 1000,
            FileSizeBytes: 1000,
            VideoStreams: [],
            AudioStreams: [],
            SubtitleStreams: [],
            Chapters: [],
            Attachments: []
        );

        Mock<IAnalysisStage> analyze = new();
        analyze
            .Setup(s =>
                s.ExecuteAsync(
                    It.IsAny<string>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new StageSuccess<MediaInfo>(info));
        Mock<IValidationStage> validate = new();
        validate
            .Setup(s =>
                s.ExecuteAsync(
                    It.IsAny<ValidateInput>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new StageSuccess<int>(0));
        Mock<IBuildStage> build = new();
        build
            .Setup(s =>
                s.ExecuteAsync(
                    It.IsAny<BuildInput>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new StageSuccess<FfmpegCommand[]>(commands));
        Mock<IExecutionStage> execute = new();
        execute
            .Setup(s =>
                s.ExecuteAsync(
                    It.IsAny<ExecuteInput>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new StageSuccess<ExecutionResult[]>([new(true, 0, "", TimeSpan.Zero, null)])
            );
        _finalize
            .Setup(s =>
                s.ExecuteAsync(
                    It.IsAny<FinalizeInput>(),
                    It.IsAny<EncodingContext>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new StageSuccess<FinalizeOutput>(new(_dir, 0)));

        NoMercy.Encoder.Pipeline.Encoder encoder = new(
            analyze.Object,
            validate.Object,
            new Mock<IPlanStage>().Object,
            build.Object,
            execute.Object,
            _finalize.Object,
            NullLogger<NoMercy.Encoder.Pipeline.Encoder>.Instance,
            resumeStitcher: new(_storage, prober, NullLogger<EncodeResumeStitcher>.Instance)
        );
        EncodingProfile profile = new(Ulid.NewUlid(), "test", Container.HlsTs, null, [], []);
        OutputPlan plan = new(OutputFormat.Hls, [], [], [], null);

        return await encoder.EncodeAsync(
            new(
                InputPath: "/media/in.mkv",
                OutputDirectory: _dir,
                Profile: profile,
                Options: new(PrecomputedPlan: plan)
            ),
            progress: null,
            ct: default
        );
    }

    /// <summary>Every segment starts at 1 s plus one second per index, or cannot be read.</summary>
    private sealed class SegmentProber(double? first) : ISegmentProber
    {
        public Task<double?> FirstPtsAsync(string segmentPath, CancellationToken ct)
        {
            int index = int.Parse(Path.GetFileNameWithoutExtension(segmentPath)[^5..]);
            return Task.FromResult(first is null ? null : first + index);
        }

        public Task<double?> FirstPacketPtsAsync(
            string segmentPath,
            bool video,
            CancellationToken ct
        ) => FirstPtsAsync(segmentPath, ct);
    }
}
