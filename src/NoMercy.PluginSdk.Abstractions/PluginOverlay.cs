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

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// Something drawn over what is playing, for as long as it is relevant.
/// <para>
/// Bounded on purpose. An overlay with no expiry is one that stays on screen
/// after the thing it described has ended, and the viewer has no way to
/// dismiss something a plugin owns.
/// </para>
/// </summary>
public sealed record PluginOverlay
{
    public required string TitleKey { get; init; }
    public string? BodyKey { get; init; }
    public IReadOnlyDictionary<string, string> Args { get; init; } =
        new Dictionary<string, string>();
    public required TimeSpan Duration { get; init; }
    public PluginRouteRef? Action { get; init; }
}
