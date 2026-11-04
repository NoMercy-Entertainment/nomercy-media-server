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
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Commands;
using NoMercy.Encoder.Decomposition;
using NoMercy.Encoder.Output;
using NoMercy.Encoder.Profiles;
using NoMercy.Encoder.Subtitles;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// FFmpeg filter-graph assembly extracted from BuildStage: scale/tonemap/deinterlace/crop
/// branch construction, burn-in expression resolution, and GPU-resident path detection.
/// Builds on the low-level fluent NoMercy.Encoder.Commands.FilterGraphBuilder.
/// </summary>
public static class FilterGraphAssembler
{
    /// <summary>
    /// Reduces a multi-variant <see cref="OutputPlan"/> down to just the
    /// outputs a single <see cref="DecomposedTask"/> needs to emit. Returns
    /// the plan unchanged for <see cref="EncodeTaskKind.Whole"/>, leaves
    /// <see cref="EncodeTaskKind.Chapters"/> to the dedicated chapter branch,
    /// and lets <see cref="EncodeTaskKind.Pass1"/> / <see cref="EncodeTaskKind.Pass2"/>
    /// keep using Pass1VariantIndex for their own slicing.
    ///
    /// When <see cref="DecomposedTask.SourceIndexes"/> is set the slice
    /// includes every entry it lists (smart-batched bucket of rungs sharing
    /// one ffmpeg via filter_complex split); otherwise the legacy single
    /// <see cref="DecomposedTask.OutputIndex"/> slice is used.
    ///
    /// Acquired subtitles are kept on Subtitle tasks (the consumer) and
    /// dropped from Video / Audio / Thumbnails tasks so they don't add
    /// spurious <c>-i</c> inputs to encodes that won't consume them.
    /// Chapters / Drm / Layout are preserved as metadata across all slices.
    /// </summary>
    /// <summary>
    /// GPU-resident when the plan carries a resolved GpuAccel plan, has video
    /// outputs, and requests no thumbnails (sprite generation needs a CPU
    /// download that would break the GPU-memory chain). Drives both the
    /// <c>-hwaccel</c> decode flags and the GPU scale branch.
    /// </summary>
    public static bool UsesGpuResidentPath(OutputPlan plan) =>
        plan.GpuAccel is not null
        && plan.Thumbnails is null
        && plan.VideoOutputs.Length > 0
        // A crop must run on the CPU filter graph before the GPU scale chain;
        // the GPU-resident path has no crop stage, so any cropped rung disqualifies it.
        && plan.VideoOutputs.All(v => string.IsNullOrWhiteSpace(v.CropFilter));

    internal static bool UsesGpuResidentPath(OutputPlan plan, MediaInfo? mediaInfo) =>
        UsesGpuResidentPath(plan)
        && mediaInfo is { VideoStreams.Count: > 0 }
        && !mediaInfo.VideoStreams[0].IsInterlaced
        && !mediaInfo.VideoStreams[0].IsAnamorphic;

    public static string? BuildFilterGraph(
        OutputPlan plan,
        MediaInfo? mediaInfo,
        string inputPath,
        AssBurnInFilterBuilder? assBurnInFilterBuilder
    )
    {
        // Only outputs with a bracketed filter_complex label participate in the
        // graph. Copy outputs carry a direct stream reference ("0:v:0") and must
        // not be routed through filter_complex — ffmpeg rejects streamcopy fed
        // from a filtergraph with exit -22.
        VideoOutputPlan[] videoOutputs = plan
            .VideoOutputs.Where(v => v.MapLabel.StartsWith('['))
            .ToArray();

        // No video outputs or no filter-graph labels — nothing to build
        if (videoOutputs.Length == 0)
            return null;

        // Source dimensions are required to decide copy vs. scale
        if (mediaInfo is null || mediaInfo.VideoStreams.Count == 0)
            return null;

        (int sourceWidth, int sourceHeight) = mediaInfo.VideoStreams[0].GetDisplayDimensions();
        bool sourceIs10Bit = mediaInfo.VideoStreams[0].BitDepth > 8;
        bool hasThumbnails = plan.Thumbnails is not null;
        bool sourceIsHdr = mediaInfo.VideoStreams[0].IsHdr;
        bool sourceIsInterlaced = mediaInfo.VideoStreams[0].IsInterlaced;
        bool sourceIsAnamorphic = mediaInfo.VideoStreams[0].IsAnamorphic;
        string? thumbnailTonemapChain = videoOutputs
            .Select(v => v.TonemapFilterChain)
            .FirstOrDefault(c => !string.IsNullOrEmpty(c));

        // Crop is a property of the SOURCE (its letterbox / pillarbox), not of
        // any single rung — every participating output resolves the same crop
        // rectangle. Hoist it to a single crop on the decoded source BEFORE the
        // split so the cropped picture feeds every consumer once: the HDR rung,
        // the tonemapped SDR rung, and the thumbnail sprite all inherit it, and
        // the expensive tonemap runs on the real picture instead of the bars.
        // Falls back to per-branch crop only if rungs somehow disagree on the
        // rectangle (never happens for a single source, but keeps the old path
        // correct if it ever did).
        string[] distinctCrops = videoOutputs
            .Where(v => !string.IsNullOrWhiteSpace(v.CropFilter))
            .Select(v => v.CropFilter!)
            .Distinct()
            .ToArray();
        string? sharedCrop = distinctCrops.Length == 1 ? distinctCrops[0] : null;

        // First text-based subtitle output with BurnIn mode (PGS burn-in uses
        // the overlay path handled before this method is called).
        string? burnInExpr = ResolveBurnInExpression(
            plan.SubtitleOutputs,
            mediaInfo,
            inputPath,
            assBurnInFilterBuilder
        );

        FilterGraphBuilder fg = new();

        // Source-level filters that every branch shares are hoisted here, once,
        // before the split — so the whole ladder (and the sprite) inherits them
        // and the work is not repeated per rung. Order matters: deinterlace
        // first (it reconstructs full frames), then crop (letterbox is measured
        // on progressive frames), then the per-branch tonemap/scale downstream.
        string baseLabel = "0:v:0";

        // Deinterlace: an interlaced source scaled without a deinterlace combs.
        // Single-rate yadif (mode 0) keeps the frame rate; every rung needs the
        // progressive frames, so it runs once on the shared source.
        if (sourceIsInterlaced)
        {
            fg.AddFilter(baseLabel, "yadif", "deint");
            baseLabel = "deint";
        }

        // Hoisted crop: run it once on the decoded source and route every
        // downstream branch from the cropped label. Per-branch crop is then
        // cleared so a rung does not crop twice.
        if (sharedCrop is not null)
        {
            fg.AddFilter(baseLabel, $"crop={sharedCrop}", "cropped");
            baseLabel = "cropped";
            videoOutputs = videoOutputs.Select(v => v with { CropFilter = null }).ToArray();
        }

        // Un-anamorph: a non-square-pixel (anamorphic) source scaled by the
        // ladder — which works in square pixels — comes out stretched. Square
        // the pixels once on the shared source (after crop, which measures coded
        // pixels), scaling width by the SAR so the display geometry is baked in
        // and SAR is reset to 1:1 for every downstream rung.
        if (sourceIsAnamorphic)
        {
            string[] sar = mediaInfo.VideoStreams[0].SampleAspectRatio!.Split(':');
            fg.AddFilter(baseLabel, $"scale=trunc(iw*{sar[0]}/{sar[1]}/2)*2:ih,setsar=1", "square");
            baseLabel = "square";
        }

        // GPU-resident path: frames are decoded into GPU memory (-hwaccel set on
        // the input) and scaled on the GPU. Eligibility guarantees no tonemap /
        // crop / burn-in, so every branch is just a GPU scale. Sprites need a CPU
        // download, so this path is skipped when thumbnails are requested.
        if (UsesGpuResidentPath(plan, mediaInfo))
        {
            string scaleFilter = plan.GpuAccel!.ScaleFilter;
            if (videoOutputs.Length == 1)
            {
                VideoOutputPlan only = videoOutputs[0];
                fg.AddGpuScale(
                    "0:v:0",
                    scaleFilter,
                    only.Width,
                    only.Height,
                    only.MapLabel.Trim('[', ']')
                );
            }
            else
            {
                List<string> splitLabels = videoOutputs.Select((_, i) => $"gsplit{i}").ToList();
                fg.AddSplit("0:v:0", splitLabels.ToArray());
                for (int i = 0; i < videoOutputs.Length; i++)
                {
                    VideoOutputPlan rung = videoOutputs[i];
                    fg.AddGpuScale(
                        splitLabels[i],
                        scaleFilter,
                        rung.Width,
                        rung.Height,
                        rung.MapLabel.Trim('[', ']')
                    );
                }
            }

            return fg.HasFilters ? fg.Build() : null;
        }

        // Tonemap once: when any SDR consumer (rung or sprite) needs the same
        // HDR→SDR tonemap chain we run it ONCE on the source and route every
        // consumer from the shared [sdr] intermediate. Per-frame the
        // zscale/tonemap chain is the most expensive filter in the graph —
        // running it per branch burns CPU for identical output, and a sprite
        // sampling raw HDR shows crushed colours. Only genuinely mixed tonemap
        // chains (different algorithms per rung) fall through to the per-branch
        // legacy path below.
        bool[] needsTonemapPerBranch = new bool[videoOutputs.Length];
        for (int i = 0; i < videoOutputs.Length; i++)
        {
            needsTonemapPerBranch[i] =
                videoOutputs[i].ConvertHdrToSdr && videoOutputs[i].TonemapFilterChain is not null;
        }

        string? sharedTonemap = null;
        bool dedupeTonemap = false;
        if (needsTonemapPerBranch.Count(needs => needs) >= 1)
        {
            string?[] tonemapChains = videoOutputs
                .Where((_, idx) => needsTonemapPerBranch[idx])
                .Select(video => video.TonemapFilterChain)
                .ToArray();
            if (tonemapChains.Distinct().Count() == 1)
            {
                sharedTonemap = tonemapChains[0];
                dedupeTonemap = true;
            }
        }

        // Total split branches: one per video output + one for thumbnails (if enabled)
        int totalBranches = videoOutputs.Length + (hasThumbnails ? 1 : 0);

        if (totalBranches == 1 && !hasThumbnails)
        {
            // Single video, no thumbnails — no split needed
            VideoOutputPlan single = videoOutputs[0];
            string outputLabel = single.MapLabel.Trim('[', ']');
            BuildBranchFilter(
                fg,
                baseLabel,
                outputLabel,
                single,
                sourceWidth,
                sourceHeight,
                sourceIs10Bit,
                burnInExpr,
                tonemapAlreadyApplied: false
            );
        }
        else if (dedupeTonemap)
        {
            // Shared HDR→SDR tonemap: run tonemap once, then split.
            //
            // Branches split into two groups:
            //   - HDR-pass branches (needsTonemap=false): use [0:v:0] directly
            //   - SDR-tonemapped branches: use [sdr] intermediate
            // Thumbnails (SDR-friendly format) branch from [sdr] when present.
            int sdrBranchCount = needsTonemapPerBranch.Count(needs => needs);
            int hdrPassBranchCount = videoOutputs.Length - sdrBranchCount;
            int thumbsFromSdr = hasThumbnails ? 1 : 0;

            int sourceSplitCount = hdrPassBranchCount + 1; // +1 for tonemap source

            List<string> sourceSplitLabels = new();
            for (int i = 0; i < hdrPassBranchCount; i++)
                sourceSplitLabels.Add($"hdr{i}");
            sourceSplitLabels.Add("tonemap_in");

            if (sourceSplitCount == 1)
            {
                // Only one downstream consumer of the source — the tonemap input.
                // No source split needed; tonemap directly from the base label.
                fg.AddFilter(baseLabel, sharedTonemap!, "sdr");
            }
            else
            {
                fg.AddSplit(baseLabel, sourceSplitLabels.ToArray());
                fg.AddFilter("tonemap_in", sharedTonemap!, "sdr");
            }

            // Split [sdr] into per-SDR-branch labels + thumbnail source.
            int sdrSplitCount = sdrBranchCount + thumbsFromSdr;
            List<string> sdrSplitLabels = new();
            for (int i = 0; i < sdrBranchCount; i++)
                sdrSplitLabels.Add($"sdr{i}");
            if (hasThumbnails)
                sdrSplitLabels.Add("thumbsrc");

            if (sdrSplitCount == 1)
            {
                // One consumer of [sdr] — feed it directly without a split.
                // Rewrite the consumer's input label later.
            }
            else
            {
                fg.AddSplit("sdr", sdrSplitLabels.ToArray());
            }

            int hdrIdx = 0;
            int sdrIdx = 0;
            string sdrInputForSingleConsumer = "sdr";
            for (int i = 0; i < videoOutputs.Length; i++)
            {
                VideoOutputPlan video = videoOutputs[i];
                string outputLabel = video.MapLabel.Trim('[', ']');
                string inputLabel;
                bool tonemapApplied;

                if (needsTonemapPerBranch[i])
                {
                    inputLabel =
                        sdrSplitCount == 1 ? sdrInputForSingleConsumer : sdrSplitLabels[sdrIdx++];
                    tonemapApplied = true;
                }
                else
                {
                    inputLabel = sourceSplitCount == 1 ? baseLabel : $"hdr{hdrIdx++}";
                    tonemapApplied = false;
                }

                BuildBranchFilter(
                    fg,
                    inputLabel,
                    outputLabel,
                    video,
                    sourceWidth,
                    sourceHeight,
                    sourceIs10Bit,
                    burnInExpr,
                    tonemapAlreadyApplied: tonemapApplied
                );
            }

            if (hasThumbnails)
            {
                ThumbnailOutputPlan thumbs = plan.Thumbnails!;
                string thumbInput = sdrSplitCount == 1 ? sdrInputForSingleConsumer : "thumbsrc";

                // Already tonemapped — this branch comes off [sdr] — so the
                // resolver is asked for the plain chain. It is asked rather than
                // hand-rolled here so the grid padding cannot be added to one
                // sprite path and forgotten on the other, which is exactly how
                // the encode path kept producing green while the rebuild did not.
                fg.AddFilter(
                    thumbInput,
                    ThumbnailFilterResolver.Resolve(
                        thumbs.IntervalSeconds,
                        thumbs.Width,
                        sourceIsHdr: false,
                        tonemapChain: null,
                        padToCells: thumbs.Grid.CellCount
                    ),
                    "thumbs"
                );
            }
        }
        else
        {
            // Legacy path: no dedupe possible (mixed tonemap params, single
            // tonemap branch, or none at all). Split source N+1 ways and let
            // each branch run its own filter chain.
            List<string> splitLabels = videoOutputs.Select((_, i) => $"split{i}").ToList();
            if (hasThumbnails)
                splitLabels.Add("thumbsrc");

            fg.AddSplit(baseLabel, splitLabels.ToArray());

            for (int i = 0; i < videoOutputs.Length; i++)
            {
                VideoOutputPlan video = videoOutputs[i];
                string outputLabel = video.MapLabel.Trim('[', ']');

                BuildBranchFilter(
                    fg,
                    splitLabels[i],
                    outputLabel,
                    video,
                    sourceWidth,
                    sourceHeight,
                    sourceIs10Bit,
                    burnInExpr,
                    tonemapAlreadyApplied: false
                );
            }

            // Thumbnail branch — uses HDR source directly when no dedupe is
            // possible (thumbnails from HDR may show crushed colors but this
            // branch only fires in the rare mixed-tonemap fallback).
            if (hasThumbnails)
            {
                ThumbnailOutputPlan thumbs = plan.Thumbnails!;
                fg.AddFilter(
                    "thumbsrc",
                    ThumbnailFilterResolver.Resolve(
                        thumbs.IntervalSeconds,
                        thumbs.Width,
                        sourceIsHdr,
                        thumbnailTonemapChain,
                        padToCells: thumbs.Grid.CellCount
                    ),
                    "thumbs"
                );
            }
        }

        return fg.HasFilters ? fg.Build() : null;
    }

    /// <summary>
    /// Builds the filter chain for a single video output branch.
    /// Pipeline: tonemap (HDR→SDR) → scale (resolution) → format (pixel format) → burn-in subs.
    /// Each step is skipped when not needed.
    ///
    /// <paramref name="tonemapAlreadyApplied"/> is set by the dedupe path
    /// in <see cref="BuildFilterGraph"/> when the source has been
    /// pre-tonemapped once (shared <c>[sdr]</c> intermediate) — this branch
    /// then skips its own tonemap chain and the implicit 8-bit conversion
    /// it carries.
    /// </summary>
    private static void BuildBranchFilter(
        IFilterGraphBuilder fg,
        string inputLabel,
        string outputLabel,
        VideoOutputPlan video,
        int sourceWidth,
        int sourceHeight,
        bool sourceIs10Bit,
        string? burnInExpr,
        bool tonemapAlreadyApplied
    )
    {
        bool needsCrop = !string.IsNullOrWhiteSpace(video.CropFilter);
        bool needsTonemap =
            !tonemapAlreadyApplied
            && video is { ConvertHdrToSdr: true, TonemapFilterChain: not null };
        bool needsScale = video.Width != sourceWidth || video.Height != sourceHeight;
        bool needs8BitConversion = !tonemapAlreadyApplied && sourceIs10Bit && !video.TenBit;
        bool needsBurnIn = burnInExpr is not null;

        if (!needsCrop && !needsTonemap && !needsScale && !needs8BitConversion && !needsBurnIn)
        {
            fg.AddFilter(inputLabel, "copy", outputLabel);
            return;
        }

        // Determine whether we need to terminate the tonemap/scale/format chain on
        // an intermediate label (because burn-in goes after) or on the output label.
        string videoChainEnd = needsBurnIn ? $"{outputLabel}_presub" : outputLabel;

        string currentLabel = inputLabel;

        // Step 0: Crop (remove letterbox / pillarbox). Must run before tonemap /
        // scale so the later filters operate on the real picture region.
        if (needsCrop)
        {
            bool hasDownstreamWork =
                needsTonemap || needsScale || needs8BitConversion || needsBurnIn;
            string cropOut = hasDownstreamWork ? $"{outputLabel}_cropped" : videoChainEnd;
            fg.AddFilter(currentLabel, $"crop={video.CropFilter}", cropOut);
            currentLabel = cropOut;

            if (!hasDownstreamWork)
                return;
        }

        // Step 1: Tonemap (HDR→SDR) — outputs yuv420p, so 8-bit conversion is included
        if (needsTonemap)
        {
            string nextLabel = needsScale ? $"{outputLabel}_tonemapped" : videoChainEnd;
            fg.AddFilter(currentLabel, video.TonemapFilterChain!, nextLabel);
            currentLabel = nextLabel;

            needs8BitConversion = false;

            if (!needsScale && !needsBurnIn)
                return;
        }

        // Step 2: Scale + format
        if (needsScale && needs8BitConversion)
        {
            string intermediate = $"{outputLabel}_scaled";
            fg.AddScaleWidth(currentLabel, video.Width, intermediate);
            fg.AddFilter(intermediate, $"format={video.PixelFormat}", videoChainEnd);
            currentLabel = videoChainEnd;
        }
        else if (needsScale)
        {
            fg.AddScaleWidth(currentLabel, video.Width, videoChainEnd);
            currentLabel = videoChainEnd;
        }
        else if (needs8BitConversion)
        {
            fg.AddFilter(currentLabel, $"format={video.PixelFormat}", videoChainEnd);
            currentLabel = videoChainEnd;
        }
        else if (needsBurnIn && !needsTonemap)
        {
            // No video processing yet — but we still need to land on the intermediate label
            // so the subtitles filter below can read from it.
            fg.AddFilter(currentLabel, "copy", videoChainEnd);
            currentLabel = videoChainEnd;
        }

        // Step 3: Burn-in subtitles — always last (after resolution is final).
        if (needsBurnIn)
        {
            fg.AddFilter(currentLabel, burnInExpr!, outputLabel);
        }
    }

    /// <summary>
    /// Resolves the first text-based burn-in subtitle output into an FFmpeg
    /// filter expression. Returns null when no burn-in subtitle is requested
    /// or when the first burn-in track is bitmap-based (PGS/DVD — those use
    /// the overlay path handled separately before <see cref="BuildFilterGraph"/>
    /// is called).
    ///
    /// <para>For ASS/SSA sources the expression uses libass via the
    /// <c>ass=</c> filter routed through <see cref="AssBurnInFilterBuilder"/>,
    /// which applies correct filter-graph escaping and threads the
    /// <c>fontsdir</c> hint when present. All other text codecs fall back to
    /// the generic <c>subtitles=</c> filter with stream-index selection.</para>
    /// </summary>
    private static string? ResolveBurnInExpression(
        SubtitleOutputPlan[] subs,
        MediaInfo mediaInfo,
        string inputPath,
        AssBurnInFilterBuilder? assBurnInFilterBuilder
    )
    {
        SubtitleOutputPlan? burnIn = subs.FirstOrDefault(s => s.Policy == SubtitlePolicy.BurnIn);
        if (burnIn is null)
            return null;

        // Only text-based codecs here — bitmap (PGS/DVD) uses the overlay path.
        if (burnIn.SourceIndex < mediaInfo.SubtitleStreams.Count)
        {
            SubtitleStreamInfo stream = mediaInfo.SubtitleStreams[burnIn.SourceIndex];
            if (!stream.IsTextBased)
                return null;

            // ASS/SSA: route through dedicated builder for libass + fontsdir.
            if (
                assBurnInFilterBuilder is not null
                && (
                    stream.Codec.Equals("ass", StringComparison.OrdinalIgnoreCase)
                    || stream.Codec.Equals("ssa", StringComparison.OrdinalIgnoreCase)
                )
            )
            {
                return assBurnInFilterBuilder.Build(inputPath);
            }
        }

        // Generic text subtitle fallback: subtitles= filter with stream-index.
        // Delegate to shared escape logic so both code paths stay in sync.
        string escaped = FilterGraphPathEscaper.Escape(inputPath);

        return $"subtitles={escaped}:si={burnIn.SourceIndex}";
    }

    /// <summary>
    /// Returns a copy of <paramref name="plan"/> with each video output's
    /// <see cref="VideoOutputPlan.MapLabel"/> replaced by its OWN pad from
    /// <paramref name="burnedLabels"/>. Used for PGS burn-in: the overlay
    /// output is <c>split</c> into one pad per rung, and each video encoder
    /// reads from a distinct pad (a filtergraph output pad can feed only one
    /// consumer, so sharing one label across rungs aborts ffmpeg).
    /// </summary>
    public static OutputPlan RemapVideoToBurnedLabels(
        OutputPlan plan,
        IReadOnlyList<string> burnedLabels
    )
    {
        VideoOutputPlan[] remapped = plan
            .VideoOutputs.Select(
                (v, i) => v with { MapLabel = burnedLabels[Math.Min(i, burnedLabels.Count - 1)] }
            )
            .ToArray();

        return plan with
        {
            VideoOutputs = remapped,
        };
    }
}
