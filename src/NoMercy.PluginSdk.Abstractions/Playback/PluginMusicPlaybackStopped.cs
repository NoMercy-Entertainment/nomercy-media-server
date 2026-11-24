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
/// The payload of <see cref="PluginTopics.MusicPlaybackStopped" />: nothing
/// plays for the listener any more. Added in ABI 12.14.
/// </summary>
/// <param name="UserId">The listener.</param>
public sealed record PluginMusicPlaybackStopped(Guid UserId);
