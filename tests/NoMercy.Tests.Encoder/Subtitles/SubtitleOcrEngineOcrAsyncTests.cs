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

using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage;

namespace NoMercy.Tests.Encoder.Subtitles;

/// <summary>
/// End-to-end arg-building and output-placement contract for
/// <see cref="SubtitleOcrEngine.OcrAsync"/>. Pins the regressions that shipped
/// bitmap-subtitle OCR completely broken in production:
///
/// • Bug 1 — the tesseract data dir and the metadata output file were placed
///   INSIDE the ffmpeg filtergraph (<c>datapath=C:/…</c>, <c>file=C:\…</c>). A
///   Windows drive colon inside a filtergraph value is unescapable — every
///   escaping attempt is parsed as an option separator — so the <c>ocr</c>
///   filter aborted at parse on every run and no <c>.vtt</c> was ever produced.
///   The data dir now rides on the <c>TESSDATA_PREFIX</c> env var and the
///   metadata file is a bare name resolved against the working directory, so no
///   colon path enters the graph. The assertions below fail if one leaks back.
///   (The prior test asserted the broken <c>datapath=C\\:/…</c> string was
///   built, never that ffmpeg could parse it — green while production was 100%
///   broken. That is the fake coverage this rewrite removes.)
/// • Bug 2 — the OCR .vtt was written next to the SOURCE file instead of the
///   encode output directory, so the post-encode library scan never registered
///   it. The sidecar assertions confirm it lands under
///   <c>{outputDirectory}/subtitles/</c>.
/// • Bug 3 — the sidecar was named <c>{lang}.ocr{streamIndex}.{ext}</c>. The
///   library scan keys subtitles by <c>{lang}.{type}</c> and pairs a bitmap
///   track with the text sidecar sharing its key, so the name has to be the
///   bitmap track's sibling — <c>{title}.{lang}.{variant}.{ext}</c>.
/// </summary>
public class SubtitleOcrEngineOcrAsyncTests
{
    private const string FfmpegPath = "/usr/bin/ffmpeg";
    private const string InputPath = "/media/movie.mkv";
    private const string ModelDirectory = "/models/tessdata";

    // ── The thread cap reaches the decoder ────────────────────────────────────

    [Fact]
    public async Task Thread_cap_sits_ahead_of_the_input()
    {
        // After -i the cap lands on the output and the decoder runs uncapped —
        // the misplacement that let a sprite pass take 10.6 cores on a budget of 2.
        string[]? args = null;
        Mock<IProcessRunner> processRunner = CaptureProcess((a, _, _) => args = a);

        SubtitleOcrEngine engine = new(
            Options(),
            processRunner.Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine.OcrAsync(InputPath, 0, "eng", SubtitleCodecType.WebVtt, default);

        args.Should().NotBeNull();
        int threadsAt = Array.IndexOf(args!, "-threads");
        int inputAt = Array.IndexOf(args!, "-i");

        threadsAt.Should().BeGreaterThan(-1);
        threadsAt.Should().BeLessThan(inputAt, "an output -threads never reaches the decoder");
    }

    // ── Filtergraph carries no colon path (Bug 1 regression) ─────────────────

    [Fact]
    public async Task Tessdata_directory_rides_in_on_env_not_the_filtergraph()
    {
        string[]? args = null;
        IReadOnlyDictionary<string, string>? env = null;
        Mock<IProcessRunner> processRunner = CaptureProcess(
            (a, e, _) =>
            {
                args = a;
                env = e;
            }
        );

        SubtitleOcrEngine engine = new(
            Options(),
            processRunner.Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine.OcrAsync(InputPath, 0, "eng", SubtitleCodecType.WebVtt, default);

        string filter = Filter(args);
        filter.Should().Contain("ocr=language=eng");
        // datapath= put the model dir into the filtergraph; a drive colon there is
        // unescapable and aborts the parse — it must never come back.
        filter.Should().NotContain("datapath");
        env.Should().ContainKey("TESSDATA_PREFIX");
        // The prefix is the directory that holds the traineddata — the same value
        // the code derives from the model path, separator-normalized per platform.
        env!
            ["TESSDATA_PREFIX"]
            .Should()
            .Be(Path.GetDirectoryName(Path.Combine(ModelDirectory, "eng.traineddata")));
    }

    [Fact]
    public async Task Windows_model_directory_never_enters_the_filtergraph()
    {
        const string windowsModelDir = @"C:\ffmpeg_build\tessdata";
        string[]? args = null;
        IReadOnlyDictionary<string, string>? env = null;
        Mock<IProcessRunner> processRunner = CaptureProcess(
            (a, e, _) =>
            {
                args = a;
                env = e;
            }
        );
        Mock<ITesseractModelManager> modelManager = new();
        modelManager
            .Setup(m => m.EnsureLanguageModelAsync("eng", It.IsAny<CancellationToken>()))
            .ReturnsAsync(Path.Combine(windowsModelDir, "eng.traineddata"));

        SubtitleOcrEngine engine = new(
            Options(),
            processRunner.Object,
            modelManager.Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine.OcrAsync(InputPath, 0, "eng", SubtitleCodecType.WebVtt, default);

        string filter = Filter(args);
        // No drive letter and no backslash-escaped drive: the graph stays
        // colon-path-free so ffmpeg can parse it. The raw path rides on the env.
        filter.Should().NotContain("C:");
        filter.Should().NotContain(@"C\");
        filter.Should().NotContain("datapath");
        env!
            ["TESSDATA_PREFIX"]
            .Should()
            .Be(Path.GetDirectoryName(Path.Combine(windowsModelDir, "eng.traineddata")));
    }

    [Fact]
    public async Task Metadata_file_is_a_bare_name_resolved_against_the_working_directory()
    {
        string[]? args = null;
        string? workingDirectory = null;
        Mock<IProcessRunner> processRunner = CaptureProcess(
            (a, _, w) =>
            {
                args = a;
                workingDirectory = w;
            }
        );

        SubtitleOcrEngine engine = new(
            Options(),
            processRunner.Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine.OcrAsync(InputPath, 0, "eng", SubtitleCodecType.WebVtt, default);

        string filter = Filter(args);
        string fileValue = filter[(filter.IndexOf("file=", StringComparison.Ordinal) + 5)..];
        // A bare name — no separators, no colon — so it can never reintroduce a
        // path into the graph. It lands in the working directory the runner is
        // handed, which must therefore be set.
        fileValue.Should().MatchRegex(@"^ocr-[0-9a-fA-F]+\.txt$");
        fileValue.Should().NotContain("/");
        fileValue.Should().NotContain(@"\");
        fileValue.Should().NotContain(":");
        workingDirectory.Should().NotBeNullOrEmpty();
    }

    // ── Output placement (Bug 2 regression) ──────────────────────────────────

    [Fact]
    public async Task No_output_directory_keeps_legacy_next_to_input_naming()
    {
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack track = await engine.OcrAsync(
            InputPath,
            0,
            "eng",
            SubtitleCodecType.WebVtt,
            default
        );

        track.FilePath.Should().Be(Path.Combine(Path.GetDirectoryName(InputPath)!, "eng_ocr.vtt"));
    }

    [Fact]
    public async Task Sidecar_is_named_as_the_bitmap_tracks_sibling()
    {
        Mock<IStorage> storage = StorageMock();
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            storage.Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack track = await engine.OcrAsync(
            InputPath,
            streamIndex: 2,
            language: "eng",
            outputFormat: SubtitleCodecType.WebVtt,
            ct: default,
            sidecar: Sidecar(storage.Object, "full")
        );

        track
            .FilePath.Should()
            .Be("/encoded/Show.S01E01/subtitles/Show.S01E01.NoMercy.eng.full.vtt");
    }

    [Fact]
    public async Task Two_streams_sharing_a_language_are_separated_by_variant()
    {
        Mock<IStorage> storage = StorageMock();
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            storage.Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack full = await engine.OcrAsync(
            InputPath,
            streamIndex: 2,
            language: "eng",
            outputFormat: SubtitleCodecType.WebVtt,
            ct: default,
            sidecar: Sidecar(storage.Object, "full")
        );
        SubtitleTrack sign = await engine.OcrAsync(
            InputPath,
            streamIndex: 5,
            language: "eng",
            outputFormat: SubtitleCodecType.WebVtt,
            ct: default,
            sidecar: Sidecar(storage.Object, "sign")
        );

        full.FilePath.Should().NotBe(sign.FilePath);
        full.FilePath.Should().EndWith("Show.S01E01.NoMercy.eng.full.vtt");
        sign.FilePath.Should().EndWith("Show.S01E01.NoMercy.eng.sign.vtt");
    }

    [Fact]
    public async Task Srt_format_keeps_the_same_sibling_naming()
    {
        Mock<IStorage> storage = StorageMock();
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            storage.Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack track = await engine.OcrAsync(
            InputPath,
            streamIndex: 1,
            language: "spa",
            outputFormat: SubtitleCodecType.Srt,
            ct: default,
            sidecar: Sidecar(storage.Object, "forced")
        );

        track
            .FilePath.Should()
            .Be("/encoded/Show.S01E01/subtitles/Show.S01E01.NoMercy.spa.forced.srt");
    }

    [Fact]
    public async Task Sidecar_is_written_through_its_own_storage_not_the_injected_one()
    {
        Mock<IStorage> destination = StorageMock();
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine.OcrAsync(
            InputPath,
            streamIndex: 0,
            language: "eng",
            outputFormat: SubtitleCodecType.WebVtt,
            ct: default,
            sidecar: Sidecar(destination.Object, "full")
        );

        destination.Verify(
            s =>
                s.WriteAsync(
                    It.Is<string>(p => p.EndsWith("eng.full.vtt")),
                    It.IsAny<byte[]>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    // AUD-1174: a track tag never leaves the output folder.
    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("..", "und")]
    public async Task Sidecar_language_with_path_segments_stays_in_the_subtitles_folder(
        string language,
        string expected
    )
    {
        Mock<IStorage> storage = StorageMock();
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            storage.Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack track = await engine.OcrAsync(
            InputPath,
            streamIndex: 2,
            language: language,
            outputFormat: SubtitleCodecType.WebVtt,
            ct: default,
            sidecar: Sidecar(storage.Object, "full")
        );

        track
            .FilePath.Should()
            .Be($"/encoded/Show.S01E01/subtitles/Show.S01E01.NoMercy.{expected}.full.vtt");
    }

    [Theory]
    [InlineData("../../x", "x")]
    [InlineData("..\\..\\x", "x")]
    [InlineData("..", "und")]
    public async Task Next_to_input_language_with_path_segments_stays_next_to_the_input(
        string language,
        string expected
    )
    {
        SubtitleOcrEngine engine = new(
            Options(),
            SuccessProcess().Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        SubtitleTrack track = await engine.OcrAsync(
            InputPath,
            0,
            language,
            SubtitleCodecType.WebVtt,
            default
        );

        track
            .FilePath.Should()
            .Be(Path.Combine(Path.GetDirectoryName(InputPath)!, $"{expected}_ocr.vtt"));
    }

    private static OcrSidecarTarget Sidecar(IStorage storage, string variant) =>
        new(storage, "/encoded/Show.S01E01", "Show.S01E01.NoMercy", variant);

    // ── Failure surfacing ─────────────────────────────────────────────────────

    [Fact]
    public async Task Nonzero_exit_throws_with_real_stderr_in_the_message()
    {
        Mock<IProcessRunner> processRunner = new();
        processRunner
            .Setup(p =>
                p.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(
                new ProcessResult(
                    1,
                    "",
                    "Error opening data file /ffmpeg_build/windows/share/tessdata/eng.traineddata",
                    TimeSpan.Zero
                )
            );

        SubtitleOcrEngine engine = new(
            Options(),
            processRunner.Object,
            ModelManagerMock().Object,
            StorageMock().Object,
            NullLogger<SubtitleOcrEngine>.Instance
        );

        await engine
            .Invoking(e => e.OcrAsync(InputPath, 0, "eng", SubtitleCodecType.WebVtt, default))
            .Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*eng.traineddata*");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string Filter(string[]? args)
    {
        args.Should().NotBeNull();
        int index = Array.IndexOf(args!, "-filter_complex");
        index.Should().BeGreaterThan(-1);
        return args![index + 1];
    }

    private static EncoderOptions Options() => new() { FfmpegPathOverride = FfmpegPath };

    private static Mock<ITesseractModelManager> ModelManagerMock()
    {
        Mock<ITesseractModelManager> modelManager = new();
        modelManager
            .Setup(m =>
                m.EnsureLanguageModelAsync(It.IsAny<string>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(Path.Combine(ModelDirectory, "eng.traineddata"));
        return modelManager;
    }

    private static Mock<IStorage> StorageMock()
    {
        Mock<IStorage> storage = new();
        storage.Setup(s => s.AcquireLocalPath(It.IsAny<string>())).Returns<string>(p => new(p));
        storage.Setup(s => s.Exists(It.IsAny<string>())).Returns(true);
        storage
            .Setup(s => s.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(Encoding.UTF8.GetBytes("pts_time:0\nlavfi.ocr.text=hi\n"));
        return storage;
    }

    private static Mock<IProcessRunner> SuccessProcess()
    {
        Mock<IProcessRunner> processRunner = new();
        processRunner
            .Setup(p =>
                p.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.Zero));
        return processRunner;
    }

    private static Mock<IProcessRunner> CaptureProcess(
        Action<string[], IReadOnlyDictionary<string, string>?, string?> capture
    )
    {
        Mock<IProcessRunner> processRunner = new();
        processRunner
            .Setup(p =>
                p.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<IReadOnlyDictionary<string, string>?>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback<
                string,
                string[],
                IReadOnlyDictionary<string, string>?,
                string?,
                CancellationToken
            >((_, args, env, workingDirectory, _) => capture(args, env, workingDirectory))
            .ReturnsAsync(new ProcessResult(0, "", "", TimeSpan.Zero));
        return processRunner;
    }
}
