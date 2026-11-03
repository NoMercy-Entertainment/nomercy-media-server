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

using NoMercy.Encoder.Commands;

namespace NoMercy.Encoder.Distribution;

/// <summary>
/// How a strategy chose to split work across workers. <see cref="QualityVariant"/>
/// encodes a full variant (one resolution / bitrate tier) per task —
/// outputs stitch together as independent HLS variants with their own
/// playlists. <see cref="TimeChunk"/> encodes the same variant across
/// multiple time ranges — outputs must be stitched into a single playlist.
/// </summary>
public enum EncodeTaskType
{
    QualityVariant,
    TimeChunk,
}

public record EncodeTask(
    string TaskId,
    FfmpegCommand Command,
    string OutputPath,
    EncodeTaskType Type,
    TimeSpan? TimeRangeStart = null,
    TimeSpan? TimeRangeDuration = null,
    /// <summary>
    /// Absolute path to the source file the task reads from. Set so remote
    /// workers can detect when the path doesn't exist locally and fall
    /// back to fetching the source from the coordinator over HTTP. Null
    /// when the task doesn't carry a file input (synthetic / live sources)
    /// or when the coordinator + workers share storage and the input path
    /// is embedded directly in <see cref="Command"/>.
    /// </summary>
    string? InputPath = null,
    /// <summary>
    /// Identifier of the variant this task produces (e.g. "1080p", "720p").
    /// Used by the orchestrator to group results from time-chunked encodes
    /// back into a single variant ladder. Empty when the task is not part
    /// of a multi-variant ladder.
    /// </summary>
    string VariantId = "",
    /// <summary>
    /// Relative compute weight used by <c>WorkerAssigner</c> when balancing
    /// tasks across workers. Higher values pull more capable workers; the
    /// scale is unitless (compare across tasks, not against a wall-clock).
    /// Defaults to 1 — strategies override for known-heavy work
    /// (e.g. 4K HEVC two-pass = 8, 1080p H.264 single-pass = 1).
    /// </summary>
    int EstimatedCostUnits = 1,
    /// <summary>
    /// True when the task requires a GPU encoder slot. Lets the assigner
    /// route GPU jobs to workers with available NVENC/QSV/VideoToolbox
    /// sessions and CPU jobs to whichever worker has spare CPU threads
    /// even if its GPU is saturated.
    /// </summary>
    bool RequiresGpu = false,
    /// <summary>
    /// Full duration of the source the task reads from. The progress
    /// denominator when the task has no time range; without it the
    /// dispatcher probes <see cref="InputPath"/> for it. Null when the
    /// caller never measured the source.
    /// </summary>
    TimeSpan? SourceDuration = null
);

public record DispatchResult(
    string TaskId,
    bool Success,
    string OutputPath,
    TimeSpan Duration,
    string? Error = null,
    /// <summary>
    /// Which worker executed the task. Null for local dispatch; populated
    /// for remote workers so the orchestrator can attribute timing + per-
    /// worker failure stats without re-querying the registry.
    /// </summary>
    string? WorkerId = null
);
