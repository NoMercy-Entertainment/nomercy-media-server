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
/// One piece of software on the owner's network that a plugin serves.
/// <para>
/// A device, never a person. The row holds what the owner called the box in
/// the living room, so they can revoke the right one; it holds nothing about
/// who was using it. The credential itself is not kept, only its hash: a copy
/// of the file is then not a copy of every device's way in.
/// </para>
/// </summary>
/// <param name="Name">What the owner called it, so they revoke the right one.</param>
/// <param name="CredentialHash">SHA-256 of the credential, base64url.</param>
public record PluginLanDevice(
    Ulid PluginId,
    string DeviceId,
    string Name,
    string CredentialHash,
    DateTimeOffset CreatedAt,
    bool Revoked
)
{
    /// <summary>Everything but the credential's hash, so a log line never carries it.</summary>
    public override string ToString() =>
        $"{nameof(PluginLanDevice)} {{ PluginId = {PluginId}, DeviceId = {DeviceId}, Name = {Name}, CreatedAt = {CreatedAt:O}, Revoked = {Revoked} }}";
}
