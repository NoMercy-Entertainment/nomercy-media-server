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

using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Pipeline.Stages;
using NoMercy.Encoder.PostProcess;
using NoMercy.Tests.Encoder.Storage;
using SubtitlePolicy = NoMercy.Encoder.Profiles.SubtitlePolicy;

namespace NoMercy.Tests.Encoder.Pipeline.Stages.Builders;

/// <summary>
/// Regression coverage for the N+1-network-reads fix: bitmap-subtitle
/// extraction and font-attachment dumping used to be N+1 separate ffmpeg
/// commands (one "-i" per bitmap subtitle stream, plus one for fonts),
/// each a full re-read of the source over the network. ExtractionCommandBuilder
/// merges all of it into ONE command with exactly one "-i".
/// </summary>
public class ExtractionCommandBuilderTests : IDisposable
{
    private const string MediaTitle = "Movie.Name.NoMercy";
    private const string InputPath = "/movies/test.mkv";

    private readonly FontExtractor _fontExtractor;
    private readonly SubtitleExtractor _subtitleExtractor = new();
    private readonly string _outputDirectory;

    public ExtractionCommandBuilderTests()
    {
        _fontExtractor = new(TestStorageFactory.CreateLocal());
        _outputDirectory = Path.Combine(
            Path.GetTempPath(),
            $"ExtractionCommandBuilderTests_{Guid.NewGuid():N}"
        );
        Directory.CreateDirectory(_outputDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_outputDirectory))
            Directory.Delete(_outputDirectory, true);
    }

    // ------------------------------------------------------------------
    // The regression that matters: N bitmap subs + M attachments still
    // open the source exactly once.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_ThreeBitmapSubsTwoAttachments_EmitsExactlyOneInputFlag()
    {
        OutputPlan plan = MakePlan(
            MakeBitmapSubtitlePlan(0),
            MakeBitmapSubtitlePlan(1),
            MakeBitmapSubtitlePlan(2)
        );
        MediaInfo mediaInfo = MakeMediaInfo(
            MakeBitmapStream(0),
            MakeBitmapStream(1),
            MakeBitmapStream(2)
        );
        IReadOnlyList<AttachmentInfo> attachments =
        [
            new(Index: 3, Codec: "ttf", Filename: "Arial.ttf", MimeType: null),
            new(Index: 4, Codec: "ttf", Filename: "Comic.ttf", MimeType: null),
        ];

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, attachments);

        command.Should().NotBeNull();
        command!.Arguments.Count(a => a == "-i").Should().Be(1);
    }

    // ------------------------------------------------------------------
    // One -map/-c:s copy/-f matroska output per eligible bitmap sub, with
    // the right .mks paths.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_BitmapSubs_EmitOneMksOutputEach()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(0), MakeBitmapSubtitlePlan(1));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0), MakeBitmapStream(1));

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().NotBeNull();
        List<string> args = [.. command!.Arguments];

        CountOccurrences(args, "-map").Should().Be(2);
        args.Should().Contain("0:s:0");
        args.Should().Contain("0:s:1");
        CountOccurrences(args, "-c:s").Should().Be(2);
        CountOccurrences(args, "copy").Should().Be(2);
        // -f appears once per bitmap output ("matroska" each time).
        CountOccurrences(args, "matroska").Should().Be(2);
        args.Should().Contain(a => a.EndsWith(".mks", StringComparison.Ordinal));
        args.Count(a => a.EndsWith(".mks", StringComparison.Ordinal)).Should().Be(2);
    }

    // ------------------------------------------------------------------
    // One -dump_attachment:{index} fonts/{safeName} per attachment;
    // sanitization + dedup preserved.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_Attachments_EmitOneDumpFlagEachWithSanitizedNames()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(0));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));
        IReadOnlyList<AttachmentInfo> attachments =
        [
            new(Index: 5, Codec: "ttf", Filename: "CM Big Fat Paintbrush_0.ttf", MimeType: null),
            new(Index: 6, Codec: "ttf", Filename: "My@Font.ttf", MimeType: null),
        ];

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, attachments);

        command.Should().NotBeNull();
        List<string> args = [.. command!.Arguments];

        args.Should().Contain("-dump_attachment:5");
        args.Should().Contain("-dump_attachment:6");
        args.Should().Contain("fonts/CM_Big_Fat_Paintbrush_0.ttf");
        args.Should().Contain(a => a.StartsWith("fonts/My", StringComparison.Ordinal));
        args.Should().NotContain(a => a.Contains(' '));
    }

    [Fact]
    public void BuildCommand_AttachmentNameCollision_StaysDisambiguated()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(0));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));
        IReadOnlyList<AttachmentInfo> attachments =
        [
            new(Index: 5, Codec: "ttf", Filename: "My Font.ttf", MimeType: null),
            new(Index: 6, Codec: "ttf", Filename: "My@Font.ttf", MimeType: null),
        ];

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, attachments);

        command.Should().NotBeNull();
        List<string> fontArgs =
        [
            .. command!.Arguments.Where(a => a.StartsWith("fonts/", StringComparison.Ordinal)),
        ];

        fontArgs.Should().OnlyHaveUniqueItems();
        fontArgs.Should().HaveCount(2);
    }

    // ------------------------------------------------------------------
    // Edge case: attachments present, no bitmap subs — sink to -f null -.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_AttachmentsOnly_SinksToNull()
    {
        OutputPlan plan = MakePlan();
        MediaInfo mediaInfo = MakeMediaInfo();
        IReadOnlyList<AttachmentInfo> attachments =
        [
            new(Index: 3, Codec: "ttf", Filename: "Arial.ttf", MimeType: null),
        ];

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, attachments);

        command.Should().NotBeNull();
        List<string> args = [.. command!.Arguments];

        args.Should().Contain("-dump_attachment:3");
        args.Should().Contain("-f");
        args.Should().Contain("null");
        args.Should().Contain("-");
        args.Should().NotContain(a => a == "-map");
    }

    // ------------------------------------------------------------------
    // Edge case: bitmap subs present, no attachments — no dump_attachment flags.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_BitmapSubsOnly_NoDumpAttachmentFlags()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(0));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().NotBeNull();
        command!.Arguments.Should().NotContain(a => a.StartsWith("-dump_attachment"));
    }

    // ------------------------------------------------------------------
    // Edge case: neither attachments nor bitmap subs — no command at all.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_NothingToExtract_ReturnsNull()
    {
        OutputPlan plan = MakePlan();
        MediaInfo mediaInfo = MakeMediaInfo();

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Gating: BurnIn policy is excluded from extraction.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_BurnInPolicy_IsExcluded()
    {
        SubtitleOutputPlan burnInPlan = MakeBitmapSubtitlePlan(0) with
        {
            Policy = SubtitlePolicy.BurnIn,
        };
        OutputPlan plan = MakePlan(burnInPlan);
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // Gating: an action other than Extract/Copy (e.g. Drop) is excluded.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_DropAction_IsExcluded()
    {
        SubtitleOutputPlan dropPlan = MakeBitmapSubtitlePlan(0) with { Action = StreamAction.Drop };
        OutputPlan plan = MakePlan(dropPlan);
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().BeNull();
    }

    // Text subtitles are extracted here, never beside the encode outputs.

    [Fact]
    public void BuildCommand_TextSubtitleStream_EmitsWebVttOutput()
    {
        SubtitleOutputPlan textPlan = new(
            OutputCodec: SubtitleCodecType.WebVtt,
            Action: StreamAction.Extract,
            Language: "en",
            SourceIndex: 0,
            MapLabel: "0:s:0"
        );
        OutputPlan plan = MakePlan(textPlan);
        MediaInfo mediaInfo = MakeMediaInfo(
            new SubtitleStreamInfo(
                Index: 0,
                Codec: "subrip",
                Language: "en",
                IsDefault: true,
                IsForced: false,
                Title: null
            )
        );

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().NotBeNull();
        List<string> args = [.. command!.Arguments];
        args.Count(a => a == "-i").Should().Be(1);
        args.Should().ContainInOrder("-map", "0:s:0", "-c:s", "webvtt");
        args.Should().Contain(a => a.EndsWith(".vtt", StringComparison.Ordinal));
        args.Should().NotContain("matroska");
        args.Should().NotContain("null");
    }

    [Fact]
    public void BuildCommand_AssSubtitleStream_EmitsAssOutput()
    {
        SubtitleOutputPlan assPlan = new(
            OutputCodec: SubtitleCodecType.Copy,
            Action: StreamAction.Copy,
            Language: "en",
            SourceIndex: 0,
            MapLabel: "0:s:0"
        );
        MediaInfo mediaInfo = MakeMediaInfo(
            new SubtitleStreamInfo(
                Index: 0,
                Codec: "ass",
                Language: "en",
                IsDefault: true,
                IsForced: false,
                Title: null
            )
        );

        FfmpegCommand? command = BuildCommand(MakePlan(assPlan), mediaInfo, []);

        command.Should().NotBeNull();
        List<string> args = [.. command!.Arguments];
        args.Should().ContainInOrder("-map", "0:s:0", "-c:s", "ass");
        args.Should().Contain(a => a.EndsWith(".ass", StringComparison.Ordinal));
    }

    // ------------------------------------------------------------------
    // Gating: a SourceIndex beyond the known subtitle streams is excluded
    // (defensive — plan/media-info drift should never crash the build).
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_SourceIndexOutOfRange_IsExcluded()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(9));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));

        FfmpegCommand? command = BuildCommand(plan, mediaInfo, []);

        command.Should().BeNull();
    }

    // ------------------------------------------------------------------
    // The merged command still targets the configured ffmpeg path, the
    // real input path, and the output directory as its working directory.
    // ------------------------------------------------------------------

    [Fact]
    public void BuildCommand_UsesFfmpegPathInputPathAndWorkingDirectory()
    {
        OutputPlan plan = MakePlan(MakeBitmapSubtitlePlan(0));
        MediaInfo mediaInfo = MakeMediaInfo(MakeBitmapStream(0));

        FfmpegCommand? command = ExtractionCommandBuilder.BuildCommand(
            "/usr/bin/ffmpeg",
            InputPath,
            _outputDirectory,
            plan,
            mediaInfo,
            MediaTitle,
            _subtitleExtractor,
            _fontExtractor,
            TestStorageFactory.CreateLocal(),
            []
        );

        command.Should().NotBeNull();
        command!.Executable.Should().Be("/usr/bin/ffmpeg");
        command.Arguments.Should().Contain(InputPath);
        command.WorkingDirectory.Should().Be(_outputDirectory);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private FfmpegCommand? BuildCommand(
        OutputPlan plan,
        MediaInfo mediaInfo,
        IReadOnlyList<AttachmentInfo> attachments
    ) =>
        ExtractionCommandBuilder.BuildCommand(
            "ffmpeg",
            InputPath,
            _outputDirectory,
            plan,
            mediaInfo,
            MediaTitle,
            _subtitleExtractor,
            _fontExtractor,
            TestStorageFactory.CreateLocal(),
            attachments
        );

    private static int CountOccurrences(List<string> args, string value) =>
        args.Count(a => a == value);

    private static OutputPlan MakePlan(params SubtitleOutputPlan[] subtitleOutputs) =>
        new(
            Format: OutputFormat.Hls,
            VideoOutputs: [],
            AudioOutputs: [],
            SubtitleOutputs: subtitleOutputs,
            Thumbnails: null
        );

    private static SubtitleOutputPlan MakeBitmapSubtitlePlan(int sourceIndex) =>
        new(
            OutputCodec: SubtitleCodecType.Copy,
            Action: StreamAction.Extract,
            Language: "en",
            SourceIndex: sourceIndex,
            MapLabel: $"0:s:{sourceIndex}"
        );

    private static SubtitleStreamInfo MakeBitmapStream(int index) =>
        new(
            Index: index,
            Codec: "hdmv_pgs_subtitle",
            Language: "en",
            IsDefault: index == 0,
            IsForced: false,
            Title: null
        );

    private static MediaInfo MakeMediaInfo(params SubtitleStreamInfo[] subtitleStreams) =>
        new(
            FilePath: InputPath,
            Format: "matroska",
            Duration: TimeSpan.FromHours(2),
            OverallBitRateKbps: 8000,
            FileSizeBytes: 7_200_000_000,
            VideoStreams: [],
            AudioStreams: [],
            SubtitleStreams: subtitleStreams,
            Chapters: []
        );
}
