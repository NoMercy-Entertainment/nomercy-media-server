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

/// <summary>The two networks the stemsplit filter separates stems with.</summary>
public enum StemsplitEngine
{
    /// <summary>Spleeter 2 stems: 39 MB, on the CPU, a few seconds per track.</summary>
    Spleeter,

    /// <summary>The RoFormer ensembles of the filter's presets: far cleaner, and only usable on a GPU.</summary>
    Roformer,
}
