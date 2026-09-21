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
/// Something to tell a viewer.
/// <para>
/// Keys, never sentences. A plugin does not know which language the reader
/// picked, and a notification stored as English text is still English when a
/// Dutch reader opens it three days later on a different device.
/// </para>
/// </summary>
public sealed record PluginNotification
{
    public required string TitleKey { get; init; }
    public required string BodyKey { get; init; }
    public IReadOnlyDictionary<string, string> Args { get; init; } =
        new Dictionary<string, string>();
    public string? Icon { get; init; }

    /// <summary>Where tapping it goes, as a route the client already knows.</summary>
    public string? Route { get; init; }
}
