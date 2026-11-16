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

using NoMercy.NmSystem.Information;

namespace NoMercy.Data.Plugins;

/// <summary>
/// What one stemsplit filter separates with, as the options that go after
/// <c>stemsplit=</c> in a graph.
/// </summary>
/// <param name="Engine">Which network.</param>
/// <param name="Options">
/// The filter options, without a trailing colon: <c>model=spleeter-2stems-f16.gguf</c>
/// or <c>preset=full:cpu_fallback=0</c>. A file name and never a path, because
/// ffmpeg runs in the folder the models are in.
/// </param>
/// <param name="ModelId">The model part of a stem's producer version: <c>spleeter-2stems-f16</c>, <c>roformer-full</c>.</param>
public sealed record StemsplitSource(StemsplitEngine Engine, string Options, string ModelId)
{
    /// <summary>The engine as the stem register stores it: <c>spleeter</c> or <c>roformer</c>.</summary>
    public string EngineName => StemEngineNames.Of(Engine);

    /// <summary>The server's Spleeter model. Computed on every read: the model name is configuration.</summary>
    public static StemsplitSource Spleeter =>
        new(
            StemsplitEngine.Spleeter,
            "model=" + PluginGraphTokens.StemsModelFileName,
            AppFiles.StemsplitModel
        );

    /// <summary>
    /// A RoFormer preset with <c>cpu_fallback=0</c>: when not every model of
    /// the run gets a GPU, the filter refuses the run rather than computing
    /// for most of an hour on the CPU, and the host falls back to Spleeter.
    /// </summary>
    public static StemsplitSource Roformer(RoformerPreset preset) =>
        new(
            StemsplitEngine.Roformer,
            $"preset={FilterName(preset)}:cpu_fallback=0",
            "roformer-" + FilterName(preset)
        );

    /// <summary>The preset's name in the filter's own options.</summary>
    public static string FilterName(RoformerPreset preset) =>
        preset switch
        {
            RoformerPreset.VocalFull => "vocal_full",
            _ => "full",
        };

    /// <summary>The model files a preset reads, without their <c>.gguf</c>.</summary>
    public static IReadOnlyList<string> Models(RoformerPreset preset) =>
        preset switch
        {
            RoformerPreset.VocalFull => AppFiles.StemsplitRoformerVocalModels,
            _ => AppFiles.StemsplitRoformerModels,
        };
}
