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

using Microsoft.Extensions.Logging;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.BuildingBlocks.Drm;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Metadata;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;
using NoMercy.Resources;
using NoMercy.Storage;
using DrmConfig = NoMercy.Encoder.BuildingBlocks.Drm.DrmConfig;

namespace NoMercy.Encoder.Pipeline.Stages;

public record BuildInput(
    ExecutionPlan Plan,
    string InputPath,
    string OutputDirectory,
    string MediaTitle,
    TimeSpan? DurationLimit = null,
    EncodingPass Pass = EncodingPass.Single,
    string? StatsFilePath = null,
    int Pass1VariantIndex = 0,
    DecomposedTask? TaskFilter = null,
    long? ResumeFromMs = null
);

public class BuildStage(
    EncoderOptions options,
    IFontExtractor fontExtractor,
    ISubtitleExtractor subtitleExtractor,
    IOutputStrategyFactory outputStrategyFactory,
    IEnumerable<IDrmProcessor> drmProcessors,
    ILogger<BuildStage> logger,
    IStorage storage,
    AssBurnInFilterBuilder? assBurnInFilterBuilder = null,
    PgsBurnInFilterBuilder? pgsBurnInFilterBuilder = null,
    IMetadataInjector? metadataInjector = null,
    IMetadataMerger? metadataMerger = null
) : IPipelineStage<BuildInput, FfmpegCommand[]>, IBuildStage
{
    public string Name => "Build";

    public async Task<StageResult> ExecuteAsync(
        BuildInput input,
        EncodingContext context,
        CancellationToken ct
    )
    {
        logger.LogInformation("[{CorrelationId}] Building FFmpeg commands", context.CorrelationId);

        try
        {
            // Use the per-folder destination storage from the job context when
            // available; fall back to the DI-injected singleton for default installs.
            IStorage effectiveStorage = context.DestinationStorage ?? storage;

            input = await ApplyDrmPreparationAsync(input, ct).ConfigureAwait(false);
            // Pass 1 of a 2-pass encode: video-only analysis to the stats file,
            // no audio / subtitles / sprite / font-extraction outputs. The
            // Pass1VariantIndex selects which variant to analyze — the strategy
            // loops 0..N-1 for multi-variant profiles.
            if (input.Pass == EncodingPass.One)
            {
                if (string.IsNullOrWhiteSpace(input.StatsFilePath))
                {
                    return new StageFailure(
                        new(
                            EncodingErrorKind.Unknown,
                            "Pass 1 requires StatsFilePath to be set.",
                            null,
                            Name,
                            false
                        )
                    );
                }

                if (input.Plan.OutputPlan.VideoOutputs.Length == 0)
                {
                    return new StageFailure(
                        new(
                            EncodingErrorKind.Unknown,
                            "2-pass requires at least one video output.",
                            null,
                            Name,
                            false
                        )
                    );
                }

                if (
                    input.Pass1VariantIndex < 0
                    || input.Pass1VariantIndex >= input.Plan.OutputPlan.VideoOutputs.Length
                )
                {
                    return new StageFailure(
                        new(
                            EncodingErrorKind.Unknown,
                            $"Pass1VariantIndex {input.Pass1VariantIndex} is out of range for "
                                + $"profile with {input.Plan.OutputPlan.VideoOutputs.Length} variants.",
                            null,
                            Name,
                            false
                        )
                    );
                }

                string variantStats = TwoPassCommandBuilder.VariantStatsPath(
                    input.StatsFilePath!,
                    input.Pass1VariantIndex
                );

                FfmpegCommand pass1 = TwoPassCommandBuilder.BuildPass1Command(
                    input.Plan.OutputPlan,
                    context.MediaInfo,
                    input.InputPath,
                    input.OutputDirectory,
                    variantStats,
                    options.FfmpegPath,
                    input.Pass1VariantIndex
                );
                return new StageSuccess<FfmpegCommand[]>([pass1]);
            }

            // Chapter-still extraction: one single-frame WebP per chapter at
            // the chapter's exact timestamp. Triggered when a Chapters task
            // filter is present and the plan carries chapter metadata.
            if (
                input.TaskFilter?.Kind == EncodeTaskKind.Chapters
                && input.Plan.OutputPlan.Chapters is not null
            )
            {
                int chapterIndex = input.TaskFilter.OutputIndex;
                if (chapterIndex < 0 || chapterIndex >= input.Plan.OutputPlan.Chapters.Count)
                {
                    return new StageFailure(
                        new(
                            EncodingErrorKind.Unknown,
                            $"Chapter index {chapterIndex} is out of range "
                                + $"(plan has {input.Plan.OutputPlan.Chapters.Count} chapters).",
                            null,
                            Name,
                            false
                        )
                    );
                }

                effectiveStorage.CreateDirectory(input.OutputDirectory);
                effectiveStorage.CreateDirectory(
                    effectiveStorage.CombinePath(input.OutputDirectory, "chapters")
                );

                ChapterInfo chapter = input.Plan.OutputPlan.Chapters[chapterIndex];
                FfmpegCommand chapterCmd = BuildChapterStillCommand(
                    options.FfmpegPath,
                    input.InputPath,
                    input.OutputDirectory,
                    chapterIndex,
                    chapter.Start
                );
                return new StageSuccess<FfmpegCommand[]>([chapterCmd]);
            }

            // Per-task slicing: when a DecomposedTask is supplied, prune the
            // OutputPlan down to the single variant this task is responsible
            // for. Without this, every task spawns an ffmpeg that emits the
            // full ladder — 8 video tasks × 16 outputs = 128 parallel
            // encoder sessions, blowing past hardware concurrency limits and
            // double-writing every output file. Chapters already filters at
            // the dedicated branch above; Pass1 / Pass2 use Pass1VariantIndex
            // for their own slicing. Whole tasks normally keep the full plan,
            // but a dispatch-time bundle with per-kind slice descriptors set
            // narrows the plan to what this batch should emit (resource cap
            // enforcement — see VideoEncodeJob.DispatchDecomposedAsync).
            bool isPerStreamSlice =
                input is
                {
                    TaskFilter:
                    {
                        Kind: EncodeTaskKind.Video
                            or EncodeTaskKind.Audio
                            or EncodeTaskKind.Subtitle
                            or EncodeTaskKind.Thumbnails
                    },
                    Pass: EncodingPass.Single
                };

            bool isBundledWhole =
                input.TaskFilter is { Kind: EncodeTaskKind.Whole } bundleFilter
                && (
                    bundleFilter.VideoSliceIndexes is not null
                    || bundleFilter.AudioSliceIndexes is not null
                    || bundleFilter.SubtitleSliceIndexes is not null
                    || bundleFilter.IncludeThumbnails is not null
                )
                && input.Pass == EncodingPass.Single;

            if (isPerStreamSlice || isBundledWhole)
            {
                DecomposedTask taskFilter = input.TaskFilter!;
                OutputPlan sliced = BuildStageSlicing.SliceForTask(
                    input.Plan.OutputPlan,
                    taskFilter
                );
                input = input with { Plan = input.Plan with { OutputPlan = sliced } };
                logger.LogInformation(
                    "[{CorrelationId}] Sliced plan for {Kind} task #{Idx}: {V} video / {A} audio / {S} sub / thumbs={T}",
                    context.CorrelationId,
                    taskFilter.Kind,
                    taskFilter.OutputIndex,
                    sliced.VideoOutputs.Length,
                    sliced.AudioOutputs.Length,
                    sliced.SubtitleOutputs.Length,
                    sliced.Thumbnails is not null
                );
            }

            IOutputStrategy strategy = outputStrategyFactory.Resolve(input.Plan.OutputPlan.Format);

            // Ensure output subdirectories exist before FFmpeg runs
            effectiveStorage.CreateDirectory(input.OutputDirectory);
            foreach (string subDir in strategy.GetOutputSubdirectories(input.Plan.OutputPlan))
            {
                effectiveStorage.CreateDirectory(
                    effectiveStorage.CombinePath(input.OutputDirectory, subDir)
                );
            }

            // Ensure subtitles/ directory exists
            if (input.Plan.OutputPlan.SubtitleOutputs.Length > 0)
            {
                effectiveStorage.CreateDirectory(
                    effectiveStorage.CombinePath(input.OutputDirectory, "subtitles")
                );
            }

            FfmpegCommandBuilder builder = new();
            builder.WithGlobalExtraFlags(input.Plan.OutputPlan.GlobalExtraFlags);

            TimeSpan? resumeSeek = ResolveResumeSeek(input.ResumeFromMs);
            bool useGpuResident = FilterGraphAssembler.UsesGpuResidentPath(
                input.Plan.OutputPlan,
                context.MediaInfo
            );
            builder.AddInput(
                new(
                    input.InputPath,
                    SeekTo: resumeSeek,
                    Duration: input.DurationLimit,
                    HwAccelDevice: useGpuResident
                        ? input.Plan.OutputPlan.GpuAccel!.HwAccelDevice
                        : null,
                    HwAccelOutputFormat: useGpuResident
                        ? input.Plan.OutputPlan.GpuAccel!.HwAccelOutputFormat
                        : null
                )
            );

            // Exact-match acquired subtitles: inject each as an additional -i input
            // so the main command can copy the subtitle stream to the output directory.
            int acquiredInputIndex = 1;
            List<(int InputIndex, AcquiredSubtitle Sub)> exactMatchSubs = [];
            IReadOnlyList<AcquiredSubtitle> acquired =
                input.Plan.OutputPlan.AcquiredSubtitles ?? [];
            foreach (AcquiredSubtitle sub in acquired)
            {
                if (!sub.IsExactMatch)
                    continue;
                builder.AddInput(new(sub.LocalPath));
                exactMatchSubs.Add((acquiredInputIndex, sub));
                acquiredInputIndex++;
            }

            // Resolve burn-in mode first so we can emit the decision log
            // entry and choose between the ASS filter path and the PGS
            // overlay path before building the filter graph.
            SubtitleOutputPlan? burnInPlan = input.Plan.OutputPlan.SubtitleOutputs.FirstOrDefault(
                s => s.Policy == SubtitlePolicy.BurnIn
            );

            if (burnInPlan is not null)
            {
                context.DecisionsOrNoOp.Add(
                    new(
                        Stage: Name,
                        Key: EncoderRuleId.SubtitlesBurnInPermanent,
                        Message: "Subtitle stream will be burned permanently into video frames. "
                            + "The resulting output cannot be toggled off by the client.",
                        Data: new { burnInPlan.SourceIndex }
                    )
                );
            }

            // For PGS burn-in: build a -filter_complex overlay chain that
            // composites the bitmap subtitle stream onto the video. The chain
            // bypasses the normal FilterGraphBuilder path so we set it directly.
            bool isPgsBurnIn =
                burnInPlan is not null
                && context.MediaInfo is not null
                && pgsBurnInFilterBuilder is not null
                && burnInPlan.SourceIndex < context.MediaInfo.SubtitleStreams.Count
                && !context.MediaInfo.SubtitleStreams[burnInPlan.SourceIndex].IsTextBased;

            string? filterGraph;

            string? pgsThumbnailLabel = null;
            if (isPgsBurnIn)
            {
                // The overlay output must be split into one distinct pad per
                // consumer — each video rung plus the thumbnail branch — or
                // ffmpeg aborts on a pad mapped more than once.
                bool pgsIncludesThumbnails = input.Plan.OutputPlan.Thumbnails is not null;
                PgsBurnInFilterChain chain = pgsBurnInFilterBuilder!.Build(
                    videoStreamIndex: 0,
                    subtitleStreamIndex: burnInPlan!.SourceIndex,
                    videoOutputCount: input.Plan.OutputPlan.VideoOutputs.Length,
                    includeThumbnails: pgsIncludesThumbnails
                );
                filterGraph = chain.FilterComplex;
                pgsThumbnailLabel = chain.ThumbnailLabel;
                // Give each video output its own split pad.
                OutputPlan pgsRemapped = FilterGraphAssembler.RemapVideoToBurnedLabels(
                    input.Plan.OutputPlan,
                    chain.VideoLabels
                );
                input = input with { Plan = input.Plan with { OutputPlan = pgsRemapped } };
            }
            else
            {
                filterGraph = FilterGraphAssembler.BuildFilterGraph(
                    input.Plan.OutputPlan,
                    context.MediaInfo,
                    input.InputPath,
                    assBurnInFilterBuilder
                );
            }

            if (filterGraph is not null)
                builder.WithFilterComplex(filterGraph);

            // Pass 2 of a 2-pass encode: inject -pass 2 + -passlogfile into each video
            // output's ExtraFlags before the output strategy emits them.
            OutputPlan effectivePlan = input.Plan.OutputPlan;
            if (input.Pass == EncodingPass.Two && !string.IsNullOrEmpty(input.StatsFilePath))
            {
                effectivePlan = TwoPassCommandBuilder.InjectPass2Flags(
                    effectivePlan,
                    input.StatsFilePath
                );
            }

            // Video + audio outputs via the output strategy (HLS, MKV, etc.)
            strategy.ConfigureOutput(builder, effectivePlan, input.OutputDirectory);

            // Acquired subtitle outputs: copy each exact-match sub to the subtitles dir.
            foreach ((int idx, AcquiredSubtitle sub) in exactMatchSubs)
            {
                string subFile = $"subtitles/{sub.Language}.acquired.{sub.Format}";
                builder.AddOutput(
                    new(FilePath: subFile, SubtitleCodec: "copy", MapStreams: [$"{idx}:s:0"])
                );
            }

            // Thumbnail sprite — the spritevtt muxer generates both the sprite
            // sheet (.webp) and the companion VTT cue file in one pass.
            List<FfmpegCommand> spriteCommands = [];
            if (input.Plan.OutputPlan.Thumbnails is not null && context.MediaInfo is not null)
            {
                ThumbnailOutputPlan thumbs = input.Plan.OutputPlan.Thumbnails;

                if (!HasFiltergraphVideoOutput(input.Plan.OutputPlan))
                {
                    // Nothing on the main builder defines a [thumbs] pad unless a
                    // video output is running through the filtergraph. That is the
                    // case for a stream-copied video output, and equally for a
                    // decomposed bundle that carries no video output at all (an
                    // audio- or subtitle-only rung): both leave the pad dangling
                    // and ffmpeg aborts the whole command with exit -22. Build the
                    // sprite as a SEPARATE command that reads the source directly
                    // through a plain -vf instead — matching the font/bitmap-subtitle
                    // pattern below. Filenames must stay byte-identical to the
                    // inline path so the VTT references the player follows still
                    // resolve.
                    //
                    // On an HDR source this bundle carries no filtergraph rung to
                    // borrow a tonemap chain from (it is stream-copy or has no
                    // video at all), so the sprite must resolve its own — a bare
                    // -vf here would sample raw HDR and produce crushed, wrong
                    // colours. ThumbnailFilterResolver is the one place that
                    // decision lives; it degrades to the plain filter untouched
                    // for an SDR source.
                    bool sourceIsHdr =
                        context.MediaInfo.VideoStreams.Count > 0
                        && context.MediaInfo.VideoStreams[0].IsHdr;
                    string? thumbnailTonemapChain = input
                        .Plan.OutputPlan.VideoOutputs.Select(v => v.TonemapFilterChain)
                        .FirstOrDefault(c => !string.IsNullOrEmpty(c));

                    spriteCommands.Add(
                        new FfmpegCommandBuilder()
                            // Global, not on the output: an output -threads reaches
                            // the encoder only, leaving the decoder to take every
                            // core — the same standalone pass the refresher runs.
                            .WithGlobalOptions(
                                new(
                                    ProgressPipe: false,
                                    Overwrite: true,
                                    Threads: EncodeThreadBudget.AuxiliaryPass
                                )
                            )
                            .AddInput(new(input.InputPath))
                            .AddOutput(
                                new(
                                    FilePath: $"thumbs_{thumbs.Width}x{thumbs.Height}.webp",
                                    MapStreams: ["0:v:0"],
                                    ExtraFlags: new()
                                    {
                                        ["-vf"] = ThumbnailFilterResolver.Resolve(
                                            thumbs.IntervalSeconds,
                                            thumbs.Width,
                                            sourceIsHdr,
                                            thumbnailTonemapChain,
                                            padToCells: thumbs.Grid.CellCount
                                        ),
                                        // The pad, the cut and the column count are
                                        // one decision in three flags: state the
                                        // columns, feed exactly enough frames to fill
                                        // them, and the muxer has no empty cell left
                                        // to render green.
                                        ["-frames:v"] = thumbs.Grid.CellCount.ToString(),
                                        ["-f"] = "spritevtt",
                                        ["-sprite_columns"] = thumbs.Grid.Columns.ToString(),
                                        ["-vtt_filename"] =
                                            $"thumbs_{thumbs.Width}x{thumbs.Height}.vtt",
                                        ["-threads"] = EncodeThreadBudget.AuxiliaryPass.ToString(),
                                        ["-filter_threads"] =
                                            EncodeThreadBudget.AuxiliaryPass.ToString(),
                                    }
                                )
                            )
                            .Build(options.FfmpegPath, input.OutputDirectory)
                    );
                }
                else
                {
                    // The normal filter graph defines a [thumbs] pad; the PGS overlay
                    // path bypasses it, so read from the dedicated split pad it
                    // produced instead — otherwise -map [thumbs] references a label
                    // that no filtergraph defines and ffmpeg aborts.
                    string thumbnailMapLabel = pgsThumbnailLabel ?? "[thumbs]";

                    builder.AddOutput(
                        new(
                            FilePath: $"thumbs_{thumbs.Width}x{thumbs.Height}.webp",
                            MapStreams: [thumbnailMapLabel],
                            ExtraFlags: new()
                            {
                                // The filtergraph branch already padded the stream
                                // out to the grid; cut it there and name the columns
                                // so the muxer lays it out exactly, with no partial
                                // last row to leave green.
                                ["-frames:v"] = thumbs.Grid.CellCount.ToString(),
                                ["-f"] = "spritevtt",
                                ["-sprite_columns"] = thumbs.Grid.Columns.ToString(),
                                ["-vtt_filename"] = $"thumbs_{thumbs.Width}x{thumbs.Height}.vtt",
                            }
                        )
                    );
                }
            }

            // Text subtitles go in the main command (single-pass). Bitmap
            // subtitles (and, when this task owns font extraction, font
            // attachments) are pulled out into ExtractionCommandBuilder below.
            if (input.Plan.OutputPlan.SubtitleOutputs.Length > 0 && context.MediaInfo is not null)
            {
                SubtitleCommandBuilder.AddTextSubtitleOutputs(
                    builder,
                    input.Plan.OutputPlan,
                    context.MediaInfo,
                    input.OutputDirectory,
                    input.MediaTitle,
                    subtitleExtractor,
                    effectiveStorage
                );
            }

            // A decomposed bundle can leave the main builder with nothing to write:
            // a Thumbnails-only task has no video or audio rung, and its sprite is
            // emitted as its own command below. ffmpeg rejects an output-less
            // invocation outright ("At least one output file must be specified",
            // exit 1), which failed the task and made VideoEncodeJob skip
            // post-encode — losing the subtitle OCR that runs there. The work still
            // happens; it just all lives in the auxiliary commands.
            List<FfmpegCommand> allCommands = [];

            // Run-once semantics: when tasks are decomposed, fonts are extracted by
            // EXACTLY ONE task to keep the on-disk fonts/ dir stable. We gate on the
            // first video task (OutputIndex=0) or the Thumbnails task when there is
            // no video. Without this gate every per-rung task would race the same
            // -dump_attachment writes, and Whole-plan executions would still extract
            // alongside their normal outputs.
            bool isFontOwner =
                input.TaskFilter is null
                || input.TaskFilter.Kind == EncodeTaskKind.Whole
                || (
                    input.TaskFilter.Kind == EncodeTaskKind.Video
                    && input.TaskFilter.OutputIndex == 0
                )
                || (
                    input.TaskFilter.Kind == EncodeTaskKind.Thumbnails
                    && input.Plan.OutputPlan.VideoOutputs.Length == 0
                );

            IReadOnlyList<AttachmentInfo> attachmentsToExtract =
                isFontOwner && context.MediaInfo is not null && context.MediaInfo.HasAttachments
                    ? context.MediaInfo.Attachments
                    : [];

            // With bitmap subtitles in the plan there is already a dedicated
            // extraction command reading the source, and the dump rides on that.
            // Without them, an attachments-only extraction command would be a
            // second full read of a multi-GB source that emits nothing but fonts —
            // so the dump rides on the encode's own read instead. Text/ASS
            // subtitles are muxed by the main command, which makes this the
            // ordinary case for subtitled anime.
            bool dumpRidesOnEncode =
                attachmentsToExtract.Count > 0
                && builder.HasOutputs
                && context.MediaInfo is not null
                && !ExtractionCommandBuilder.HasBitmapSubtitleOutputs(
                    input.Plan.OutputPlan,
                    context.MediaInfo,
                    input.OutputDirectory,
                    input.MediaTitle,
                    subtitleExtractor,
                    effectiveStorage
                );

            if (builder.HasOutputs)
            {
                FfmpegCommand mainCommand = builder.Build(
                    options.FfmpegPath,
                    input.OutputDirectory
                );
                bool copyMode = IsCopyMode(input.Plan.OutputPlan);
                mainCommand = MetadataInjectionBuilder.InjectMetadataArgs(
                    metadataInjector,
                    metadataMerger,
                    mainCommand,
                    context.MediaItem,
                    context,
                    copyMode,
                    context.EnableMetadataInjection
                );

                if (dumpRidesOnEncode)
                    mainCommand = ExtractionCommandBuilder.InjectAttachmentDumpArgs(
                        mainCommand,
                        input.OutputDirectory,
                        attachmentsToExtract,
                        fontExtractor,
                        effectiveStorage
                    );

                logger.LogInformation(
                    "[{CorrelationId}] FFmpeg command: {Executable} {Args}",
                    context.CorrelationId,
                    mainCommand.Executable,
                    string.Join(" ", mainCommand.Arguments)
                );

                allCommands.Add(mainCommand);
            }
            else
            {
                logger.LogInformation(
                    "[{CorrelationId}] No encoded output in this bundle; running only its auxiliary commands.",
                    context.CorrelationId
                );
            }

            allCommands.AddRange(spriteCommands);

            // Bitmap subtitles, plus font attachments when the encode command did
            // not already carry them, are pulled out into ONE dedicated ffmpeg
            // command. input.InputPath is frequently a remote source (NFS/SMB/S3),
            // so every extra "-i" is a full network re-read of a multi-GB file —
            // merging what used to be one command per bitmap subtitle stream plus
            // one for fonts turns N+1 network reads into exactly one. Kept separate
            // from the main encode command so a subtitle extraction failure never
            // sinks an otherwise successful, hours-long encode.
            IReadOnlyList<AttachmentInfo> attachmentsForExtractionCommand = dumpRidesOnEncode
                ? []
                : attachmentsToExtract;

            if (
                context.MediaInfo is not null
                && (
                    input.Plan.OutputPlan.SubtitleOutputs.Length > 0
                    || attachmentsForExtractionCommand.Count > 0
                )
            )
            {
                FfmpegCommand? extractionCommand = ExtractionCommandBuilder.BuildCommand(
                    options.FfmpegPath,
                    input.InputPath,
                    input.OutputDirectory,
                    input.Plan.OutputPlan,
                    context.MediaInfo,
                    input.MediaTitle,
                    subtitleExtractor,
                    fontExtractor,
                    effectiveStorage,
                    attachmentsForExtractionCommand
                );

                if (extractionCommand is not null)
                    allCommands.Add(extractionCommand);
            }

            return new StageSuccess<FfmpegCommand[]>([.. allCommands]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StageFailure(
                new(
                    EncodingErrorKind.Unknown,
                    $"Command build failed: {ex.Message}",
                    null,
                    Name,
                    false
                )
            );
        }
    }

    /// <summary>
    /// When the profile ships a DRM config, resolve the matching processor,
    /// generate key + keyinfo artifacts in the output directory, and splice
    /// <c>-hls_key_info_file</c> into every video output's extra flags so
    /// the output strategy emits it on the ffmpeg command. Returns the input
    /// unchanged when DRM is disabled or no processor handles the method.
    /// </summary>
    private async Task<BuildInput> ApplyDrmPreparationAsync(BuildInput input, CancellationToken ct)
    {
        DrmConfig? drm = input.Plan.OutputPlan.Drm;
        if (drm is null || drm.Method == DrmMethod.None)
            return input;

        IDrmProcessor? processor = drmProcessors.FirstOrDefault(p => p.Method == drm.Method);
        if (processor is null)
        {
            // DRM was explicitly requested by the profile — proceeding without
            // it would ship an unencrypted output while reporting success.
            // Fail the encode instead of silently downgrading to plaintext.
            throw new InvalidOperationException(
                $"DRM was requested ({drm.Method}) but no matching processor is registered — "
                    + "refusing to ship an unencrypted encode."
            );
        }

        DrmArtifact artifact = await processor
            .PrepareAsync(input.OutputDirectory, drm, ct)
            .ConfigureAwait(false);

        VideoOutputPlan[] encryptedVideos =
        [
            .. input.Plan.OutputPlan.VideoOutputs.Select(v =>
            {
                Dictionary<string, string> extra = new(v.ExtraFlags)
                {
                    ["-hls_key_info_file"] = artifact.KeyInfoFilePath,
                };
                return v with { ExtraFlags = extra };
            }),
        ];

        OutputPlan newOutputPlan = input.Plan.OutputPlan with { VideoOutputs = encryptedVideos };
        ExecutionPlan newPlan = input.Plan with { OutputPlan = newOutputPlan };
        return input with { Plan = newPlan };
    }

    /// <summary>
    /// Returns true when any video output uses the "copy" pseudo-encoder or
    /// any audio output uses <see cref="StreamAction.Copy"/>, meaning source
    /// streams are passed byte-for-byte without re-encoding.
    /// </summary>
    internal static bool IsCopyMode(OutputPlan plan)
    {
        foreach (VideoOutputPlan v in plan.VideoOutputs)
        {
            if (string.Equals(v.EncoderName, "copy", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        foreach (AudioOutputPlan a in plan.AudioOutputs)
        {
            if (a.Action == StreamAction.Copy)
                return true;
        }

        return false;
    }

    /// <summary>
    /// True only when EVERY video output in the plan is stream-copied — the
    /// pure-remux case where no bracket-labeled (filtergraph) video output
    /// exists at all, so a "[thumbs]" pad on the main builder has nothing to
    /// reference. A mixed ladder (one rung smart-copied, another transcoded —
    /// see PlanStage.ApplySmartCopyDowngrade) still has a live filtergraph for
    /// the transcoded rung, so the inline "[thumbs]" split stays valid there;
    /// only a fully-copied plan needs the sprite pulled into a separate
    /// command. Deliberately narrower than <see cref="IsCopyMode"/>, which
    /// also flags audio-only copy and would wrongly reroute a plan whose
    /// video is still transcoded.
    /// </summary>
    internal static bool AllVideoOutputsAreCopy(OutputPlan plan) =>
        plan.VideoOutputs.Length > 0
        && plan.VideoOutputs.All(v =>
            string.Equals(v.EncoderName, "copy", StringComparison.OrdinalIgnoreCase)
        );

    /// <summary>
    /// True when at least one video output is encoded through the filtergraph, and
    /// so defines the pads (<c>[thumbs]</c>) that other outputs on the same command
    /// map. False both when every video output is a stream copy and when the plan
    /// carries no video output at all — a decomposed audio- or subtitle-only bundle.
    /// Callers must not attach filtergraph-fed outputs to the main builder when this
    /// is false; ffmpeg rejects the whole command with exit -22.
    /// </summary>
    internal static bool HasFiltergraphVideoOutput(OutputPlan plan) =>
        plan.VideoOutputs.Length > 0 && !AllVideoOutputsAreCopy(plan);

    /// <summary>
    /// Converts a crash-checkpoint progress position into an input seek
    /// TimeSpan, backing off by a fixed keyframe window to ensure ffmpeg
    /// can land on a clean keyframe rather than a mid-GOP position.
    /// Returns null when ResumeFromMs is null or zero (no seek required).
    /// </summary>
    internal static TimeSpan? ResolveResumeSeek(long? resumeFromMs)
    {
        if (resumeFromMs is null or <= 0)
            return null;

        const long keyframeBackoffMs = 4000;
        long seekMs = Math.Max(0, resumeFromMs.Value - keyframeBackoffMs);
        return TimeSpan.FromMilliseconds(seekMs);
    }

    /// <summary>
    /// Builds a single-frame WebP extraction command for one chapter still.
    /// Output: <c>chapters/{index:D2}.webp</c> at 240 px wide, aspect-preserved.
    /// <c>-ss</c> before <c>-i</c> (input seek) is used for accuracy on keyframe-
    /// aligned chapter timestamps; the output is a single frame so decode cost
    /// is minimal regardless of seek position.
    /// </summary>
    internal static FfmpegCommand BuildChapterStillCommand(
        string ffmpegPath,
        string inputPath,
        string outputDirectory,
        int chapterIndex,
        TimeSpan timestamp
    )
    {
        // Forward-slash separator — matches HLS / sprite / audio / video output paths
        // across the codebase. Windows ffmpeg accepts forward slashes; using
        // Path.Combine here would emit backslashes that break manifest references.
        string outputFile = $"chapters/{chapterIndex:D2}.webp";

        return new FfmpegCommandBuilder()
            .WithGlobalOptions(new(ProgressPipe: false, Overwrite: true))
            .AddInput(new(FilePath: inputPath, SeekTo: timestamp))
            .AddOutput(
                new(
                    FilePath: outputFile,
                    VideoCodec: "libwebp",
                    ExtraFlags: new() { ["-frames:v"] = "1", ["-vf"] = "scale=240:-2" }
                )
            )
            .Build(ffmpegPath, outputDirectory);
    }
}
