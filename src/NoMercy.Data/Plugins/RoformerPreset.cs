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
/// The stemsplit presets the host runs RoFormer with. Each is a separate
/// question to the filter: a card that holds the two vocals models may not
/// hold all four.
/// </summary>
public enum RoformerPreset
{
    /// <summary><c>preset=full</c>: four models, the best of both stems. What a stems job runs.</summary>
    Full,

    /// <summary>
    /// <c>preset=vocal_full</c>: the two vocals models, the accompaniment as
    /// the mixture minus the vocals. Half the memory of <see cref="Full" />,
    /// and the same lyric line times.
    /// </summary>
    VocalFull,
}
