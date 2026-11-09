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

using System.Globalization;
using NoMercy.Encoder.Analysis;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.PostProcess;
using NoMercy.Encoder.Profiles;
using NoMercy.Storage;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// Builds ONE ffmpeg command that covers font-attachment dumping and every
/// subtitle file (text and bitmap) for a single input. Sources typically live on
/// remote storage (NFS/SMB/S3), so every extra "-i" re-reads the whole
/// multi-GB file over the network — merging what used to be N+1 separate
/// commands (one per bitmap subtitle stream, plus one for font attachments)
/// into a single pass turns N+1 network reads into exactly one.
///
/// Kept as its own dedicated command whenever there is real extraction work
/// (bitmap subtitles) to do: the main encode can run for hours, and a failure
/// copying subtitles must never sink an otherwise-successful encode.
///
/// Attachments alone are the exception. With no bitmap subtitles the command
/// degenerates to "-i source -f null -", a full read of a multi-GB source that
/// produces nothing but fonts, so the dump is spliced into the encode's own
/// source read instead (<see cref="InjectAttachmentDumpArgs"/>). Attachment
/// names are sanitized up front, which removes the one failure mode that
/// aborts ffmpeg on a dump; anything left (disk full, permissions) would sink
/// the encode's own output regardless. A dump that still comes up short is
/// repaired by FinalizeStage re-running it standalone, rather than by
/// discarding the encode.
/// </summary>
public static class ExtractionCommandBuilder
{
    /// <summary>
    /// Returns null when there is nothing to extract (no attachments, no
    /// bitmap subtitles) — callers must not spawn a pointless ffmpeg process.
    /// </summary>
    public static FfmpegCommand? BuildCommand(
        string ffmpegPath,
        string inputPath,
        string outputDirectory,
        OutputPlan plan,
        MediaInfo mediaInfo,
        string mediaTitle,
        ISubtitleExtractor subtitleExtractor,
        IFontExtractor fontExtractor,
        IStorage storage,
        IReadOnlyList<AttachmentInfo> attachments,
        TimeSpan? durationLimit = null
    )
    {
        List<string> args = ["-y", "-hide_banner"];

        if (attachments.Count > 0)
            AddAttachmentDumpArgs(args, outputDirectory, attachments, fontExtractor, storage);

        List<(string OutputPath, string Codec, int SourceIndex)> subtitleOutputs =
            ResolveSubtitleOutputs(
                plan,
                mediaInfo,
                outputDirectory,
                mediaTitle,
                subtitleExtractor,
                storage
            );

        if (attachments.Count == 0 && subtitleOutputs.Count == 0)
            return null;

        // Preview runs cap the source read; a resume seek is deliberately not
        // applied so the extracted files always cover the whole title.
        if (durationLimit.HasValue)
        {
            args.Add("-t");
            args.Add(durationLimit.Value.TotalSeconds.ToString("F3", CultureInfo.InvariantCulture));
        }

        args.Add("-i");
        args.Add(inputPath);

        if (subtitleOutputs.Count == 0)
        {
            // Attachments only — no real stream output, so ffmpeg needs a
            // sink to write to. Matches the previous font-only extraction.
            args.Add("-f");
            args.Add("null");
            args.Add("-");
        }
        else
        {
            AddSubtitleOutputArgs(args, subtitleOutputs);
        }

        return new(ffmpegPath, args.ToArray(), outputDirectory);
    }

    /// <summary>
    /// True when this input has subtitle files to extract, which means a
    /// dedicated extraction command already exists to carry the attachment
    /// dump. When false the dump has no run to ride along with here, and
    /// <see cref="InjectAttachmentDumpArgs"/> folds it into the encode instead
    /// of spawning a source read that produces nothing but fonts.
    /// </summary>
    public static bool HasSubtitleOutputs(
        OutputPlan plan,
        MediaInfo mediaInfo,
        string outputDirectory,
        string mediaTitle,
        ISubtitleExtractor subtitleExtractor,
        IStorage storage
    ) =>
        ResolveSubtitleOutputs(
            plan,
            mediaInfo,
            outputDirectory,
            mediaTitle,
            subtitleExtractor,
            storage
        ).Count > 0;

    /// <summary>
    /// Splices the pre-input <c>-dump_attachment</c> flags into an already-built
    /// command so the attachments come out of a source read that was happening
    /// anyway. The flags are input options, so they go before the FIRST "-i" —
    /// they attach to the source, never to a later input.
    /// </summary>
    public static FfmpegCommand InjectAttachmentDumpArgs(
        FfmpegCommand command,
        string outputDirectory,
        IReadOnlyList<AttachmentInfo> attachments,
        IFontExtractor fontExtractor,
        IStorage storage
    )
    {
        if (attachments.Count == 0)
            return command;

        int inputFlag = Array.IndexOf(command.Arguments, "-i");
        if (inputFlag < 0)
            return command;

        List<string> dumpArgs = [];
        AddAttachmentDumpArgs(dumpArgs, outputDirectory, attachments, fontExtractor, storage);

        List<string> args = [.. command.Arguments];
        args.InsertRange(inputFlag, dumpArgs);

        return command with
        {
            Arguments = args.ToArray(),
        };
    }

    // -dump_attachment is a pre-input flag, so these must be added before
    // "-i". Passing an explicit, sanitized output filename per attachment —
    // rather than the bulk -dump_attachment:t "" form, which reuses each
    // attachment's embedded filename — stops a single attachment whose
    // embedded name ffmpeg rejects as "unsafe" from aborting the whole dump.
    // Naming/dedup stays owned by IFontExtractor so a plugin's replacement
    // implementation controls it too.
    private static void AddAttachmentDumpArgs(
        List<string> args,
        string outputDirectory,
        IReadOnlyList<AttachmentInfo> attachments,
        IFontExtractor fontExtractor,
        IStorage storage
    )
    {
        string fontDir = storage.CombinePath(outputDirectory, "fonts");
        storage.CreateDirectory(fontDir);

        foreach (
            AttachmentDumpTarget target in fontExtractor.ResolveAttachmentDumpTargets(attachments)
        )
        {
            args.Add($"-dump_attachment:{target.Index}");
            args.Add(target.RelativePath);
        }
    }

    private static void AddSubtitleOutputArgs(
        List<string> args,
        List<(string OutputPath, string Codec, int SourceIndex)> outputs
    )
    {
        foreach ((string outputPath, string codec, int sourceIndex) in outputs)
        {
            args.Add("-map");
            args.Add($"0:s:{sourceIndex}");
            args.Add("-c:s");
            args.Add(codec);
            if (outputPath.EndsWith(".mks", StringComparison.OrdinalIgnoreCase))
            {
                // Must specify -f matroska explicitly — FFmpeg doesn't auto-detect .mks.
                args.Add("-f");
                args.Add("matroska");
            }
            args.Add(outputPath);
        }
    }

    // Bitmap subs (dvd_subtitle, PGS) can't be muxed to .sub+.idx in a
    // multi-output command, so they go to MKS. Text subs (WebVTT, ASS, SRT)
    // keep their extractor-resolved codec and path.
    private static List<(string OutputPath, string Codec, int SourceIndex)> ResolveSubtitleOutputs(
        OutputPlan plan,
        MediaInfo mediaInfo,
        string outputDirectory,
        string mediaTitle,
        ISubtitleExtractor subtitleExtractor,
        IStorage storage
    )
    {
        List<(string OutputPath, string Codec, int SourceIndex)> outputs = [];

        foreach (SubtitleOutputPlan subPlan in plan.SubtitleOutputs)
        {
            if (subPlan.Policy == SubtitlePolicy.BurnIn)
                continue;

            if (subPlan.Action is not (StreamAction.Extract or StreamAction.Copy))
                continue;

            if (subPlan.SourceIndex >= mediaInfo.SubtitleStreams.Count)
                continue;

            SubtitleStreamInfo stream = mediaInfo.SubtitleStreams[subPlan.SourceIndex];

            SubtitleOutputInfo info = subtitleExtractor.ResolveOutput(
                subPlan,
                stream,
                outputDirectory,
                mediaTitle
            );

            // Ensure subtitle directory exists (storage-relative parent of OutputPath).
            string? parentDir = storage.GetParent(info.OutputPath);
            if (parentDir is not null)
                storage.CreateDirectory(storage.CombinePath(outputDirectory, parentDir));

            if (stream.IsTextBased)
            {
                outputs.Add((info.OutputPath, info.FfmpegCodec, info.SourceIndex));
                continue;
            }

            // Use MKS (Matroska) container for bitmap subs.
            string outputPath = Path.ChangeExtension(info.OutputPath, ".mks");

            outputs.Add((outputPath, "copy", info.SourceIndex));
        }

        return outputs;
    }
}
