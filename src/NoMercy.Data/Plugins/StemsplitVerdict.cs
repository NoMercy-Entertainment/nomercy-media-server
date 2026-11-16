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

using Newtonsoft.Json;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The stemsplit filter's refusal of RoFormer on this machine, and the
/// machine it was given on.
/// </summary>
/// <param name="FfmpegBuild">The ffmpeg binary's size and time, which change with every release the server installs.</param>
/// <param name="Gpus">The hash of the GPU list with each driver version.</param>
/// <param name="RefusedPresets">The presets' filter names, or <see cref="EveryPreset" />.</param>
/// <param name="Reason">The filter's own line, for the owner reading the file.</param>
/// <param name="RefusedAt">When.</param>
/// <param name="AskAgainAfter">
/// When the filter is asked again even on the same machine: a refusal for
/// want of free memory depends on what else held the GPU at that moment, a
/// game for one. Null for a refusal for want of any GPU, which only a new
/// ffmpeg build, GPU or driver changes.
/// </param>
internal sealed record StemsplitVerdict(
    [property: JsonProperty("ffmpeg_build")] string FfmpegBuild,
    [property: JsonProperty("gpus")] string Gpus,
    [property: JsonProperty("refused_presets")] IReadOnlyList<string> RefusedPresets,
    [property: JsonProperty("reason")] string Reason,
    [property: JsonProperty("refused_at")] DateTimeOffset RefusedAt,
    [property: JsonProperty("ask_again_after")] DateTimeOffset? AskAgainAfter = null
)
{
    /// <summary>Every preset: the filter found no GPU it could use at all.</summary>
    public const string EveryPreset = "*";

    /// <summary>
    /// Whether the verdict refuses <paramref name="preset" />. <c>full</c>
    /// holds every model the other presets have, so a refusal of any of them
    /// for its memory is a refusal of <c>full</c> too.
    /// </summary>
    public bool Refuses(RoformerPreset preset)
    {
        string name = StemsplitSource.FilterName(preset);

        return RefusedPresets.Contains(EveryPreset)
            || RefusedPresets.Contains(name)
            || (preset == RoformerPreset.Full && RefusedPresets.Count > 0);
    }

    /// <summary>
    /// Whether the verdict was given on the machine as it is now.
    /// <paramref name="gpusUnknown" /> is a GPU list that came back empty:
    /// the detection swallows its own failures into an empty list, so an
    /// empty list is no evidence that the GPUs changed. Had they really all
    /// gone, Spleeter would be the answer anyway.
    /// </summary>
    public bool Holds(string ffmpegBuild, string gpus, bool gpusUnknown) =>
        FfmpegBuild == ffmpegBuild && (gpusUnknown || Gpus == gpus);

    /// <summary>Whether the time to ask again has come.</summary>
    public bool HasExpired(DateTimeOffset now) => AskAgainAfter is { } after && now >= after;
}
