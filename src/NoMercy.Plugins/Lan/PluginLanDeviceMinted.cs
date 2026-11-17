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

namespace NoMercy.PluginSdk.Lan;

/// <summary>
/// A device just added, with its credential in the clear: the one time the
/// server has it. The store keeps only a hash, so this is what the dashboard
/// shows the owner once, and nothing reads it back later.
/// </summary>
public sealed record PluginLanDeviceMinted(
    Ulid PluginId,
    string DeviceId,
    string Name,
    string Credential,
    DateTimeOffset CreatedAt
)
{
    /// <summary>Everything but the credential, so a log line never carries it.</summary>
    public override string ToString() =>
        $"{nameof(PluginLanDeviceMinted)} {{ PluginId = {PluginId}, DeviceId = {DeviceId}, Name = {Name}, CreatedAt = {CreatedAt:O} }}";
}
