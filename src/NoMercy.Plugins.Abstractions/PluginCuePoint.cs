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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// One place in a track a transition could sensibly start or land, found by
/// analysis rather than guessed from the beat grid alone.
/// </summary>
/// <param name="Ms">Milliseconds from the start of the track.</param>
/// <param name="Type">One of "intro", "drop", "breakdown" or "outro" - what kind of section boundary this is.</param>
/// <param name="Direction">One of "mixIn" or "mixOut" - whether this point suits entering or leaving the track.</param>
/// <param name="Score">0..1, how strong a candidate this is relative to the track's other cue points.</param>
public sealed record PluginCuePoint(int Ms, string Type, string Direction, double Score);
