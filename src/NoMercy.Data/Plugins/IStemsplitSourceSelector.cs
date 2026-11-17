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

namespace NoMercy.Data.Plugins;

/// <summary>
/// Chooses between RoFormer and Spleeter for a stem split on this machine.
/// <para>
/// Whether RoFormer runs is the stemsplit filter's own verdict, not the
/// host's: the host offers RoFormer wherever its models are installed, and
/// the filter refuses a run whose models do not all get a GPU. That refusal
/// is remembered here, so the next split goes straight to Spleeter, until the
/// ffmpeg build, the GPU list or a GPU driver changes and the filter is asked
/// again.
/// </para>
/// </summary>
public interface IStemsplitSourceSelector
{
    /// <summary>
    /// RoFormer with <paramref name="preset" /> when its models are installed
    /// and the filter has not refused it on this machine as it is now;
    /// Spleeter otherwise.
    /// </summary>
    Task<StemsplitSource> ChooseAsync(RoformerPreset preset, CancellationToken ct = default);

    /// <summary>
    /// Remembers that the filter refused a run of <paramref name="presets" />
    /// with <paramref name="refusalLine" />. A refusal for want of any GPU
    /// counts for every preset; one for want of memory counts for the preset
    /// with that many models, and for every larger one.
    /// </summary>
    Task RememberRefusalAsync(
        IReadOnlyCollection<RoformerPreset> presets,
        string refusalLine,
        CancellationToken ct = default
    );
}
