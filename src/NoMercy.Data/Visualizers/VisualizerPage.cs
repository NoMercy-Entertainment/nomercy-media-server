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

using NoMercy.Database.Models.Media;

namespace NoMercy.Data.Visualizers;

/// <summary>One page of list rows (payload left empty) with the caller's links to them.</summary>
public sealed record VisualizerPage(
    IReadOnlyList<Visualizer> Items,
    IReadOnlyDictionary<Ulid, VisualizerUser> Links,
    bool HasMore
);
