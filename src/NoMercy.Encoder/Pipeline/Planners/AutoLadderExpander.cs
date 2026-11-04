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
using NoMercy.Encoder.Profiles;

namespace NoMercy.Encoder.Pipeline.Stages;

/// <summary>
/// Auto-ladder expansion extracted from PlanStage: turns an Auto ladder
/// profile into explicit Manual ABR rungs via the supplied generator.
/// </summary>
public static class AutoLadderExpander
{
    /// <summary>
    /// When the profile opts into <see cref="LadderMode.Auto"/>, expand the
    /// single reference video output into a multi-tier ABR ladder generated
    /// from the source media's resolution + bitrate density. The generated
    /// outputs are stored as Manual rungs so <see cref="PlanStageHelpers.EnumerateVideo"/>
    /// materialises them correctly on subsequent passes.
    /// Passthrough when auto-ladder is off or when the source has no video.
    /// </summary>
    public static EncodingProfile Expand(
        IAbrLadderGenerator abrLadderGenerator,
        ILogger logger,
        EncodingProfile profile,
        MediaInfo media
    )
    {
        if (profile.Ladder?.Mode != LadderMode.Auto || media.VideoStreams.Count == 0)
            return profile;

        LadderRung[] existingRungs = profile.Ladder.Rungs ?? [];

        // Auto + multiple rungs → keep rungs as-is, switch to Manual
        if (existingRungs.Length > 1)
        {
            logger.LogWarning(
                "AutoLadder with {Count} rungs: falling back to Manual mode.",
                existingRungs.Length
            );
            return profile with
            {
                Ladder = new() { Mode = LadderMode.Manual, Rungs = existingRungs },
            };
        }

        VideoOutput? reference =
            profile.Video
            ?? (
                existingRungs.Length == 1
                    ? PlanStageHelpers.BuildSyntheticReference(existingRungs[0])
                    : null
            );

        if (reference is null)
        {
            logger.LogWarning(
                "AutoLadder requires a reference Video output or at least one rung; profile has neither. Falling back to no video outputs."
            );
            return profile;
        }

        LadderRung[] rungs;

        if (profile.Ladder.AutoConfig is not null)
        {
            rungs = abrLadderGenerator.GenerateLadder(
                media,
                reference.Codec,
                profile.Ladder.AutoConfig,
                reference
            );
        }
        else
        {
            VideoOutput[] ladder = abrLadderGenerator.Generate(media, reference);
            if (ladder.Length == 0)
                return profile;

            rungs =
            [
                .. ladder.Select(v => new LadderRung(
                    // v.Width null (or legacy 0) means "keep source width" —
                    // a ladder rung always carries a concrete resolution.
                    Width: v.Width is int w and > 0
                        ? w
                        : media.VideoStreams[0].GetDisplayDimensions().Width,
                    Height: v.Height ?? 0,
                    Codec: v.Codec,
                    BitrateKbps: v.BitrateKbps,
                    MaxBitrateKbps: v.MaxBitrateKbps ?? 0,
                    BufferSizeKbps: v.BufferSizeKbps ?? 0,
                    Framerate: 0,
                    Preset: v.Preset,
                    CodecProfile: v.CodecProfile,
                    BitDepth: v.BitDepth,
                    PixelFormat: v.PixelFormat
                )),
            ];
        }

        if (rungs.Length == 0)
            return profile;

        logger.LogInformation(
            "AutoLadder expanded 1 reference profile → {Count} variants for {Source}",
            rungs.Length,
            media.FilePath
        );

        return profile with
        {
            Ladder = new() { Mode = LadderMode.Manual, Rungs = rungs },
        };
    }
}
