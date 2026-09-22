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
/// A named group of channels, in the order the provider gave them. Order is
/// carried because a provider's own ordering is what a viewer recognises.
/// </summary>
public sealed record PluginChannelGroup
{
    public required string Name { get; init; }
    public required IReadOnlyList<string> ChannelIds { get; init; }
    public int? SortOrder { get; init; }
}
