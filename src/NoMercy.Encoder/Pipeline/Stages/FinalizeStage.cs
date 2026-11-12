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
using Microsoft.Extensions.Logging;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Bundle;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.Errors;
using NoMercy.Encoder.Execution;
using NoMercy.Encoder.Infrastructure;
using NoMercy.Encoder.Naming;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Reconciliation;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Stages;

public record FinalizeInput(
    ExecutionResult[] Results,
    OutputPlan Plan,
    string OutputDirectory,
    string MediaTitle,
    IProgressObserver? Progress = null,
    // Null means "use spec defaults" — not "skip everything".
    HlsDerivatives? HlsDerivatives = null,
    // The RESOLVED profile actually applied to this encode. Used to stamp
    // manifest.json with a profile fingerprint so a later reconciliation
    // pass can tell "preset edited in place, same id" apart from "genuinely
    // unchanged". Null for callers that predate reconciliation (Preview) —
    // the manifest is then written without a fingerprint, same as any
    // pre-reconciliation output.
    EncodingProfile? Profile = null
);

public record FinalizeOutput(string OutputPath, long OutputSizeBytes);

public class FinalizeStage(
    IChapterWriter chapterWriter,
    IFontExtractor fontExtractor,
    IOutputStrategyFactory outputStrategyFactory,
    EncoderOptions options,
    IProcessRunner processRunner,
    ILogger<FinalizeStage> logger,
    IStorage storage,
    IMediaBlueprintWriter? blueprintWriter = null
) : IPipelineStage<FinalizeInput, FinalizeOutput>, IFinalizeStage
{
    public string Name => "Finalize";

    public async Task<StageResult> ExecuteAsync(
        FinalizeInput input,
        EncodingContext context,
        CancellationToken ct
    )
    {
        logger.LogInformation("[{CorrelationId}] Finalizing output", context.CorrelationId);

        try
        {
            // Use the per-folder destination storage from the job context when
            // available; fall back to the DI-injected singleton for default installs.
            IStorage effectiveStorage = context.DestinationStorage ?? storage;

            // Null → spec defaults. Never skip everything.
            HlsDerivatives derivatives = input.HlsDerivatives ?? new HlsDerivatives();

            effectiveStorage.CreateDirectory(input.OutputDirectory);

            IOutputStrategy strategy = outputStrategyFactory.Resolve(input.Plan.Format);

            // GenerateMasterPlaylist — master playlist written by the output strategy.
            // The flag currently gates the whole FinalizeAsync call because the output
            // strategies pair playlist writing with segment finalization; teasing the
            // two apart is a strategy-level change, not a finalize-stage concern.
            if (derivatives.GenerateMasterPlaylist)
            {
                input.Progress?.OnStageStarted("Building Master Playlist");
                await strategy.FinalizeAsync(
                    input.OutputDirectory,
                    input.Plan,
                    input.MediaTitle,
                    ct
                );
                input.Progress?.OnStageCompleted("Building Master Playlist", TimeSpan.Zero);
            }

            // GenerateChapters — write chapters.vtt from MediaInfo.
            if (
                derivatives.GenerateChapters
                && context.MediaInfo is not null
                && context.MediaInfo.Chapters.Count > 0
            )
            {
                input.Progress?.OnStageStarted("Extracting chapters");
                await chapterWriter.WriteChaptersAsync(
                    input.OutputDirectory,
                    context.MediaInfo.Chapters,
                    ct,
                    includeThumbUris: derivatives.GenerateChapterThumbs
                );
                input.Progress?.OnStageCompleted("Extracting chapters", TimeSpan.Zero);

                logger.LogDebug(
                    "[{CorrelationId}] Wrote {Count} chapters to chapters.vtt",
                    context.CorrelationId,
                    context.MediaInfo.Chapters.Count
                );
            }

            // GenerateFontsJson — write fonts.json manifest from previously extracted fonts.
            if (derivatives.GenerateFontsJson)
            {
                input.Progress?.OnStageStarted("Extracting fonts");
                int fontsWritten = await fontExtractor.WriteFontManifestAsync(
                    input.OutputDirectory,
                    ct
                );
                input.Progress?.OnStageCompleted("Extracting fonts", TimeSpan.Zero);

                // Completeness gate: a source with embedded fonts whose extraction
                // came up short would render subtitles with missing glyphs. Fail
                // the encode instead of publishing broken output — the orchestrator
                // only publishes to the destination when the result is successful.
                int expectedFonts = context.MediaInfo is null
                    ? 0
                    : fontExtractor.CountFontAttachments(context.MediaInfo.Attachments);

                // Fonts are dumped by whichever run already reads the source, so a
                // short count means everything else in the bundle is present and
                // only the attachment dump fell short. Discarding an hours-long
                // encode to recover a few KB of fonts is the wrong trade: re-run
                // the dump on its own — it is the one part that failed — and only
                // fail when it is STILL short.
                if (expectedFonts > 0 && fontsWritten < expectedFonts)
                    fontsWritten = await RedumpAttachmentsAsync(input, context, fontsWritten, ct);

                if (expectedFonts > 0 && fontsWritten < expectedFonts)
                    return new StageFailure(
                        new(
                            EncodingErrorKind.Unknown,
                            $"Font extraction incomplete: source has {expectedFonts} embedded font(s) "
                                + $"but only {fontsWritten} were extracted. Subtitle rendering would be "
                                + "missing fonts, so the output is not published.",
                            null,
                            Name,
                            true
                        )
                    );
            }
            else
            {
                logger.LogDebug(
                    "[{CorrelationId}] Skipping fonts.json (GenerateFontsJson=false)",
                    context.CorrelationId
                );
            }

            // Sprite VTT, thumbnail track, original-filename tag, and metadata.json
            // sidecar are all produced upstream in BuildStage — except for the cues
            // covering the black tiles the grid was padded out with, which only
            // exist once the sheet has been written.
            await TrimSpritePaddingCuesAsync(input, context, effectiveStorage, ct);

            //
            // The opt-in flags below have no implementation yet. Fail loudly instead
            // of silently ignoring a profile that set them, so callers get a clear
            // error instead of a successful encode missing the requested artifact.
            if (derivatives.GenerateIFramePlaylists)
                throw new NotSupportedException(
                    "HlsDerivatives.GenerateIFramePlaylists is set but no IFramePlaylistGenerator "
                        + "is wired. Leave it false until I-frame playlist support lands."
                );

            if (derivatives.ExtractClosedCaptions)
                throw new NotSupportedException(
                    "HlsDerivatives.ExtractClosedCaptions is set but no CcExtractor is wired. "
                        + "Leave it false until CEA-608/708 extraction lands."
                );

            // SubtitleImsc is reserved for future work — log a warning instead
            // of throwing so existing profiles created before WebVtt/Imsc were
            // wired (when the toggle was a no-op) don't suddenly hard-fail.
            // The dashboard label flags it as "not yet implemented" so users
            // see the same intent on the editor side.
            if (derivatives.SubtitleImsc)
                logger.LogWarning(
                    "[{CorrelationId}] HlsDerivatives.SubtitleImsc is true but IMSC subtitle output is not implemented — flag ignored. Untick it in the encoder profile to silence this warning.",
                    context.CorrelationId
                );

            if (context.MediaInfo is not null && Directory.Exists(input.OutputDirectory))
            {
                IReadOnlyList<string> short_ = EncodeOutputValidator.ValidateDurations(
                    input.OutputDirectory,
                    context.MediaInfo.EncodedVideoDuration.TotalSeconds
                );
                if (short_.Count > 0)
                    throw new InvalidDataException(
                        $"Encode output is truncated ({short_.Count} rendition(s)): {string.Join("; ", short_)}"
                    );
            }

            IReadOnlyList<StorageEntry> allEntries = effectiveStorage.List(
                input.OutputDirectory,
                "*",
                recursive: true
            );

            long totalSize = allEntries.Where(e => !e.IsDirectory).Sum(e => e.SizeBytes);

            // Emit .nomercy.json when the encode has a resolved BundleLayout and
            // the writer is wired (DI singleton). Skipped when layout is null
            // (legacy callers that don't set MediaItem on the context).
            if (
                input.Plan.Layout is BundleLayout layout
                && blueprintWriter is not null
                && context.MediaInfo is not null
            )
                await WriteBlueprintAsync(
                    effectiveStorage,
                    input.OutputDirectory,
                    layout,
                    input.Plan,
                    allEntries,
                    context,
                    input.Profile,
                    ct
                );

            return new StageSuccess<FinalizeOutput>(new(input.OutputDirectory, totalSize));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new StageFailure(
                new(
                    EncodingErrorKind.Unknown,
                    $"Finalization failed: {ex.Message}",
                    null,
                    Name,
                    false
                )
            );
        }
    }

    /// <summary>
    /// Drops the cues pointing at the sheet's padding tiles.
    ///
    /// <para>The grid is deliberately over-estimated, so the last few tiles are
    /// black frames past the end of the film. They cost nothing in the scrub bar,
    /// which only ever asks for a time inside the film — but a television draws
    /// the whole cue list as a filmstrip, and there the padding shows as black
    /// frames tacked onto the end.</para>
    ///
    /// <para>Best-effort: the sheet is already written and correct, and a VTT that
    /// still lists its padding is a cosmetic fault on one surface. It is not worth
    /// failing a finished encode over.</para>
    /// </summary>
    /// <summary>
    /// Runs the attachment dump as its own ffmpeg command and rewrites
    /// fonts.json, returning the new font count. Used only as a repair when the
    /// dump that rode along with an earlier run came up short — the standalone
    /// command re-reads the source, which is why it is never the first choice.
    /// Returns the caller's original count when there is nothing to retry with
    /// or the retry itself fails, so the completeness gate still fails the
    /// encode rather than publishing output with missing glyphs.
    /// </summary>
    private async Task<int> RedumpAttachmentsAsync(
        FinalizeInput input,
        EncodingContext context,
        int fontsWritten,
        CancellationToken ct
    )
    {
        if (context.MediaInfo is null || string.IsNullOrWhiteSpace(context.InputPath))
        {
            logger.LogWarning(
                "[{CorrelationId}] Font extraction came up short but the source path is "
                    + "unknown, so the attachment dump cannot be retried.",
                context.CorrelationId
            );
            return fontsWritten;
        }

        logger.LogWarning(
            "[{CorrelationId}] Font extraction came up short; re-running the attachment "
                + "dump on its own instead of failing the encode.",
            context.CorrelationId
        );

        FfmpegCommand command = fontExtractor.BuildExtractionCommand(
            options.FfmpegPath,
            context.InputPath,
            input.OutputDirectory,
            context.MediaInfo.Attachments
        );

        // ffmpeg writes each attachment relative to the command's working
        // directory (fonts/), and will not create it itself.
        if (command.WorkingDirectory is { } workingDirectory)
            (context.DestinationStorage ?? storage).CreateDirectory(workingDirectory);

        ProcessResult result = await processRunner.RunAsync(
            command.Executable,
            command.Arguments,
            command.WorkingDirectory,
            ct
        );

        if (!result.IsSuccess)
        {
            logger.LogWarning(
                "[{CorrelationId}] Attachment re-dump exited {ExitCode}: {StdErr}",
                context.CorrelationId,
                result.ExitCode,
                result.StdErr
            );
            return fontsWritten;
        }

        return await fontExtractor.WriteFontManifestAsync(input.OutputDirectory, ct);
    }

    private async Task TrimSpritePaddingCuesAsync(
        FinalizeInput input,
        EncodingContext context,
        IStorage effectiveStorage,
        CancellationToken ct
    )
    {
        if (input.Plan.Thumbnails is not { } thumbs || context.MediaInfo is null)
            return;

        string vttPath = effectiveStorage.CombinePath(
            input.OutputDirectory,
            $"thumbs_{thumbs.Width}x{thumbs.Height}.vtt"
        );

        try
        {
            if (!effectiveStorage.Exists(vttPath))
                return;

            byte[] raw = await effectiveStorage.ReadAsync(vttPath, ct);
            string trimmed = SpriteVttTrimmer.Trim(
                Encoding.UTF8.GetString(raw),
                context.MediaInfo.Duration
            );

            await effectiveStorage.WriteAsync(vttPath, Encoding.UTF8.GetBytes(trimmed), ct);
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "[{CorrelationId}] Could not trim the scrub preview's padding cues; the sheet is fine, the filmstrip will show a few black tiles past the end",
                context.CorrelationId
            );
        }
    }

    private async Task WriteBlueprintAsync(
        IStorage effectiveStorage,
        string outputDirectory,
        BundleLayout layout,
        OutputPlan plan,
        IReadOnlyList<StorageEntry> allEntries,
        EncodingContext context,
        EncodingProfile? profile,
        CancellationToken ct
    )
    {
        // Relative to the media folder, which is where the encoder actually writes
        // video_*/, audio_*/ and the rest — allEntries comes from listing
        // input.OutputDirectory, so entry.Path is already in that domain.
        string dirPrefix = outputDirectory.TrimEnd('/') + "/";

        List<string> relFiles = [];
        foreach (StorageEntry entry in allEntries)
        {
            if (entry.IsDirectory)
                continue;
            string rel = entry.Path.StartsWith(dirPrefix, StringComparison.OrdinalIgnoreCase)
                ? entry.Path[dirPrefix.Length..]
                : entry.Path;
            // Exclude the blueprint itself from its own file list.
            if (rel.Equals(MediaBlueprintWriter.FileName, StringComparison.OrdinalIgnoreCase))
                continue;
            relFiles.Add(rel);
        }

        string encoderVersion = typeof(FinalizeStage).Assembly.GetName().Version?.ToString() ?? "3";
        // The real media folder this encode ends up in — recorded INSIDE the file
        // as output_location. NOT where the file is written: every encode runs in a
        // staging temp dir and PublishTempDirAsync sweeps that dir's contents to the
        // real folder afterward. Writing to OriginalOutputDirectory here put the
        // blueprint outside the temp dir, so publish never carried it and it never
        // reached the media folder.
        string mediaRoot = context.OriginalOutputDirectory ?? outputDirectory;

        // Layout is only ever resolved (PlanStage) when context.MediaItem is set —
        // see OutputNamingResolver.Resolve's caller — so MediaItem is guaranteed
        // non-null here.
        //
        // Write to outputDirectory (the staging temp dir where video_*/, audio_*/
        // etc. are assembled) so the blueprint publishes to the media root exactly
        // like every rendition does. originalSourcePath carries the real source, not
        // the staging lease MediaInfo.FilePath points at.
        await blueprintWriter!.WriteAsync(
            effectiveStorage,
            outputDirectory,
            context.MediaInfo!,
            BlueprintIdentityFactory.From(context.MediaItem!),
            plan,
            layout,
            relFiles,
            outputLocation: mediaRoot,
            encoderVersion: encoderVersion,
            profileFingerprint: profile is not null ? ProfileFingerprint.Compute(profile) : null,
            createdAt: DateTime.UtcNow,
            completedAt: DateTime.UtcNow,
            ct,
            originalSourcePath: context.OriginalInputPath
        );

        logger.LogInformation(
            "[{CorrelationId}] Wrote {FileName} into staging {StagingDir} ({FileCount} files); publishes to media root {MediaRoot}",
            context.CorrelationId,
            MediaBlueprintWriter.FileName,
            outputDirectory,
            relFiles.Count,
            mediaRoot
        );
    }
}
