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

namespace NoMercy.Plugins.Lan;

/// <summary>
/// One piece of software on the owner's network that a plugin serves.
/// <para>
/// A device, never a person. The row holds what the owner called the box in
/// the living room, so they can revoke the right one; it holds nothing about
/// who was using it.
/// </para>
/// </summary>
/// <param name="Name">What the owner called it, so they revoke the right one.</param>
public record PluginLanDevice(
    Ulid PluginId,
    string DeviceId,
    string Name,
    string Credential,
    DateTimeOffset CreatedAt,
    bool Revoked
);
