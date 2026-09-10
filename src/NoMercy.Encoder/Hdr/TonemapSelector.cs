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

using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Hardware;
using NoMercy.Encoder.Pipeline;
using NoMercy.Storage;
using NoMercy.Storage.Validation;
using ProfileHdrOptions = NoMercy.Encoder.Profiles.HdrOptions;

namespace NoMercy.Encoder.Hdr;

public class TonemapSelector : ITonemapSelector
{
    private static readonly HashSet<string> KnownAlgorithms = new(StringComparer.OrdinalIgnoreCase)
    {
        "hable",
        "mobius",
        "reinhard",
        "clip",
        "bt2390",
    };

    /// <summary>
    /// The CPU tonemap chain, owned here because this is where it is chosen.
    /// <para>Anything that needs SDR pixels from an HDR source without a GPU
    /// filter reads this rather than repeating it. A second copy elsewhere is a
    /// copy that drifts: the sprite sheets tonemapped with one chain while the
    /// video used another, and nothing said so.</para>
    /// </summary>
    public const string CpuTonemapChain =
        "zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,"
        + "tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

    public TonemapStrategy SelectBest(
        IHardwareCapabilities hardware,
        IFfmpegCapabilities? ffmpeg = null
    )
    {
        // Priority: libplacebo (Vulkan GPU) → tonemap_opencl (OpenCL) → zscale+tonemap (CPU)
        if (ffmpeg is not null && ffmpeg.HasFilter("libplacebo"))
            return new(
                TonemapMethod.Libplacebo,
                "libplacebo=tonemapping=hable:color_primaries=bt709:color_trc=bt709:colorspace=bt709:format=yuv420p",
                true
            );

        if (ffmpeg is not null && ffmpeg.HasFilter("tonemap_opencl"))
            return new(
                TonemapMethod.TonemapOpencl,
                "tonemap_opencl=tonemap=hable:desat=0:format=nv12",
                true
            );

        return new(TonemapMethod.ZscaleTonemap, CpuTonemapChain, false);
    }

    /// <inheritdoc/>
    public async Task<TonemapPlan> BuildAsync(
        ProfileHdrOptions? options,
        string? profileTonemapAlgorithm,
        IDecisionLogSink decisions,
        IStorage? storage = null,
        CancellationToken cancellationToken = default
    )
    {
        // --- Algorithm resolution -------------------------------------------
        string rawAlgorithm = options?.Algorithm ?? profileTonemapAlgorithm ?? "hable";

        string algorithm;
        if (KnownAlgorithms.Contains(rawAlgorithm))
        {
            algorithm = rawAlgorithm.ToLowerInvariant();
        }
        else
        {
            decisions.Add(
                new(
                    "plan",
                    "plan.tonemap_unknown_algorithm_defaulted",
                    $"Unknown tonemap algorithm '{rawAlgorithm}' — falling back to hable",
                    new { requested = rawAlgorithm, fallback = "hable" }
                )
            );
            algorithm = "hable";
        }

        // --- Peak nits -------------------------------------------------------
        int peakNits = options?.PeakNits ?? 100;

        // --- LUT path --------------------------------------------------------
        string? lutPath = options?.LutPath;
        if (lutPath is not null && storage is not null)
        {
            // Validate through IStorage's path guard (sync lease).
            // AcquireLocalPath throws StoragePathNotAllowedException when the path
            // is outside the configured allowed roots or fails structural checks.
            try
            {
                await using LocalPathLease lease = await storage
                    .AcquireLocalPathAsync(lutPath, cancellationToken)
                    .ConfigureAwait(false);

                string lutFilter = $"lut3d={FilterGraphPathEscaper.Escape(lutPath)}";

                decisions.Add(
                    new(
                        "plan",
                        "plan.tonemap_resolved",
                        $"Tonemap resolved: LUT path '{lutPath}' (algorithm={algorithm}, nits={peakNits}, lut=true)",
                        new
                        {
                            algorithm,
                            peakNits,
                            usedLut = true,
                            lutPath,
                        }
                    )
                );

                return new(
                    Algorithm: algorithm,
                    PeakNits: peakNits,
                    LutFilterChain: lutFilter,
                    FilterStringFragment: lutFilter
                );
            }
            catch (StoragePathNotAllowedException ex)
            {
                decisions.Add(
                    new(
                        "plan",
                        "plan.tonemap_lut_path_rejected",
                        $"LUT path '{lutPath}' rejected ({ex.Message}) — falling back to algorithm '{algorithm}'",
                        new
                        {
                            lutPath,
                            reason = ex.Message,
                            fallbackAlgorithm = algorithm,
                        }
                    )
                );
                // Fall through to algorithm-based filter.
            }
        }

        // --- Algorithm-based zscale+tonemap chain ----------------------------
        string fragment =
            $"zscale=t=linear:npl={peakNits},format=gbrpf32le,zscale=p=bt709,tonemap=tonemap={algorithm}:desat=0,zscale=t=bt709:m=bt709:r=tv,format=yuv420p";

        decisions.Add(
            new(
                "plan",
                "plan.tonemap_resolved",
                $"Tonemap resolved: algorithm={algorithm}, nits={peakNits}, lut=false",
                new
                {
                    algorithm,
                    peakNits,
                    usedLut = false,
                }
            )
        );

        return new(
            Algorithm: algorithm,
            PeakNits: peakNits,
            LutFilterChain: null,
            FilterStringFragment: fragment
        );
    }
}
