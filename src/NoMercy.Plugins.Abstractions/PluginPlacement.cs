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
/// Where one of a plugin's pages appears.
/// </summary>
/// <param name="Kind">One of <see cref="PluginKind.All" />.</param>
/// <param name="Slot">One of <see cref="PluginSlots.All" /> for that kind.</param>
/// <param name="Label">A translation key, never a sentence.</param>
/// <param name="Icon">A name from the shared icon set, or null for none.</param>
/// <param name="Route">Where it goes, as a route and its values.</param>
/// <param name="Surfaces">
/// Which surfaces draw it. Empty means all of them, because a plugin that has
/// not thought about televisions should still appear on one rather than
/// silently not.
/// </param>
public sealed record PluginPlacement(
    string Kind,
    string Slot,
    string Label,
    string? Icon,
    PluginRouteRef Route,
    IReadOnlyList<string> Surfaces
);
