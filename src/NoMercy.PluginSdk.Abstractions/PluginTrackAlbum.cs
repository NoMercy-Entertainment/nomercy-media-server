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
/// One album a track is on, as <see cref="PluginTrack.Albums" /> lists it.
/// Added in ABI 12.15.
/// </summary>
/// <param name="Id">The album's id on this server.</param>
/// <param name="Name">The album's name.</param>
public sealed record PluginTrackAlbum(Guid Id, string Name);
