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
/// One artist of a track, as <see cref="PluginTrack.Artists" /> lists it.
/// Added in ABI 12.15.
/// </summary>
/// <param name="Id">The artist's id on this server.</param>
/// <param name="Name">The artist's name.</param>
public sealed record PluginTrackArtist(Guid Id, string Name);
