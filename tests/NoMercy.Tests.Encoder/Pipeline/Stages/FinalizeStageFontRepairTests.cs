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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Storage;

namespace NoMercy.Tests.Encoder.Pipeline.Stages;

/// <summary>
/// A short font count means the attachment dump fell short while every other
/// artifact in the bundle landed. Failing the stage there discards an
/// hours-long encode to recover a few KB, so the dump — the one part that
/// failed — is re-run on its own and only a STILL-short result fails.
/// </summary>
public class FinalizeStageFontRepairTests : IDisposable
{
    private const string SourcePath = "/movies/anime.mkv";

    private readonly string _outputDirectory;
    private readonly string _fontDirectory;
    private readonly Mock<IProcessRunner> _processRunner = new();

    public FinalizeStageFontRepairTests()
    {
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"FinalizeStageFontRepairTests_{Guid.NewGuid():N}"
        );
        _fontDirectory = Path.Combine(_outputDirectory, "fonts");
        Directory.CreateDirectory(_fontDirectory);

        // One of the two embedded fonts made it out; the dump came up short.
        File.WriteAllBytes(Path.Combine(_fontDirectory, "Arial.ttf"), [0x00, 0x01]);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, true);
    }

    // ------------------------------------------------------------------
    // The re-dump recovers the missing font → the encode is published.
    // ------------------------------------------------------------------

    [Fact]
    public async Task ShortFontCount_RedumpRecoversTheFont_StageSucceeds()
    {
        SetupRunner(exitCode: 0, writesFont: "Comic.ttf");

        StageResult result = await RunFinalizeAsync();

        result.Should().BeOfType<StageSuccess<FinalizeOutput>>();
        File.Exists(Path.Combine(_outputDirectory, "fonts.json")).Should().BeTrue();
        File.ReadAllText(Path.Combine(_outputDirectory, "fonts.json"))
            .Should()
            .Contain("Comic.ttf");
    }

    [Fact]
    public async Task ShortFontCount_RunsTheDumpExactlyOnce()
    {
        SetupRunner(exitCode: 0, writesFont: "Comic.ttf");

        await RunFinalizeAsync();

        // One repair attempt, not a retry loop — the source read is the cost.
        _processRunner.Verify(
            r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    [Fact]
    public async Task ShortFontCount_DumpCarriesTheSourceAndBothAttachments()
    {
        SetupRunner(exitCode: 0, writesFont: "Comic.ttf");

        await RunFinalizeAsync();

        _processRunner.Verify(
            r =>
                r.RunAsync(
                    "ffmpeg",
                    It.Is<string[]>(args =>
                        args.Contains(SourcePath)
                        && args.Contains("-dump_attachment:3")
                        && args.Contains("-dump_attachment:4")
                    ),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Once
        );
    }

    // ------------------------------------------------------------------
    // A re-dump that recovers nothing must still fail the stage — publishing
    // output whose subtitles render with missing glyphs is the worse outcome.
    // ------------------------------------------------------------------

    [Fact]
    public async Task RedumpRecoversNothing_StageStillFails()
    {
        SetupRunner(exitCode: 1, writesFont: null);

        StageResult result = await RunFinalizeAsync();

        result.Should().BeOfType<StageFailure>();
        ((StageFailure)result).Error.Message.Should().Contain("Font extraction incomplete");
    }

    // ------------------------------------------------------------------
    // A complete dump must not trigger a repair at all — the standalone
    // command is a second full source read.
    // ------------------------------------------------------------------

    [Fact]
    public async Task CompleteFontCount_NeverRunsTheDump()
    {
        File.WriteAllBytes(Path.Combine(_fontDirectory, "Comic.ttf"), [0x00, 0x02]);
        SetupRunner(exitCode: 0, writesFont: null);

        StageResult result = await RunFinalizeAsync();

        result.Should().BeOfType<StageSuccess<FinalizeOutput>>();
        _processRunner.Verify(
            r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    // Every parameter is supplied explicitly: IProcessRunner.RunAsync declares
    // optional parameters, and a Moq setup that omits them never matches the
    // fully-specified call the stage makes.
    private void SetupRunner(int exitCode, string? writesFont)
    {
        _processRunner
            .Setup(r =>
                r.RunAsync(
                    It.IsAny<string>(),
                    It.IsAny<string[]>(),
                    It.IsAny<string?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(() =>
            {
                if (writesFont is not null)
                    File.WriteAllBytes(Path.Combine(_fontDirectory, writesFont), [0x00, 0x02]);

                return new ProcessResult(
                    ExitCode: exitCode,
                    StdOut: string.Empty,
                    StdErr: exitCode == 0 ? string.Empty : "attachment dump failed",
                    Duration: TimeSpan.Zero
                );
            });
    }

    private async Task<StageResult> RunFinalizeAsync()
    {
        FinalizeStage stage = new(
            new ChapterWriter(TestStorageFactory.CreateLocal()),
            new FontExtractor(TestStorageFactory.CreateLocal()),
            OutputStrategyFactoryTestHelper.Create(),
            new EncoderOptions { FfmpegPathOverride = "ffmpeg", FfprobePathOverride = "ffprobe" },
            _processRunner.Object,
            NullLogger<FinalizeStage>.Instance,
            TestStorageFactory.CreateLocal()
        );

        FinalizeInput input = new(
            Results:
            [
                new(
                    Success: true,
                    ExitCode: 0,
                    StdErr: string.Empty,
                    Duration: TimeSpan.Zero,
                    Error: null
                ),
            ],
            Plan: new(
                Format: OutputFormat.Hls,
                VideoOutputs: [],
                AudioOutputs: [],
                SubtitleOutputs: [],
                Thumbnails: null
            ),
            OutputDirectory: _outputDirectory,
            MediaTitle: "Anime.NoMercy",
            HlsDerivatives: new()
            {
                GenerateMasterPlaylist = false,
                GenerateChapters = false,
                GenerateFontsJson = true,
            }
        );

        EncodingContext context = EncodingContext.Create() with
        {
            InputPath = SourcePath,
            MediaInfo = new(
                FilePath: SourcePath,
                Format: "matroska",
                Duration: TimeSpan.FromMinutes(24),
                OverallBitRateKbps: 8000,
                FileSizeBytes: 1_200_000_000,
                VideoStreams: [],
                AudioStreams: [],
                SubtitleStreams: [],
                Chapters: [],
                Attachments:
                [
                    new(Index: 3, Codec: "ttf", Filename: "Arial.ttf", MimeType: null),
                    new(Index: 4, Codec: "ttf", Filename: "Comic.ttf", MimeType: null),
                ]
            ),
        };

        return await stage.ExecuteAsync(input, context, CancellationToken.None);
    }
}
