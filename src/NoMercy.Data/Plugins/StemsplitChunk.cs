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

/// <summary>One RoFormer chunk the stemsplit filter finished, as its log line reports it.</summary>
/// <param name="Model">Which model of the run, from 1.</param>
/// <param name="Models">How many models the run has.</param>
/// <param name="Chunk">Which chunk of the input, from 1.</param>
/// <param name="Chunks">How many chunks the input has; null while the filter does not know yet.</param>
/// <param name="Filter">
/// Which stemsplit filter of the graph printed it, as ffmpeg names the
/// instance in front of the line (<c>Parsed_stemsplit_1</c>); null for a line
/// without that name.
/// </param>
internal sealed record StemsplitChunk(
    int Model,
    int Models,
    long Chunk,
    long? Chunks,
    string? Filter = null
)
{
    /// <summary>The last chunk of the last model: the filter has handed out all it will.</summary>
    public bool IsLast => Model == Models && Chunks is { } total && Chunk >= total;
}
