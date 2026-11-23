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
/// The payload of <see cref="PluginTopics.MusicPlaybackDeviceChanged" />:
/// another device makes the sound for the listener. Added in ABI 12.14.
/// </summary>
/// <param name="UserId">The listener.</param>
/// <param name="DeviceId">The device that took over; null when the session was released and no device holds it.</param>
public sealed record PluginMusicPlaybackDeviceChanged(Guid UserId, string? DeviceId);
