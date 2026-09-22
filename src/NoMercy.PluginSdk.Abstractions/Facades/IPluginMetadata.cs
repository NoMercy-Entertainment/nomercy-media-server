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
/// Asking the providers the owner already configured.
/// <para>
/// A plugin that carried its own API key spent its own rate limit, asked
/// providers the owner had turned off, and kept working after the owner revoked
/// the capability, because the key was inside the plugin rather than beside the
/// grant. Asking the server fixes all three.
/// </para>
/// </summary>
public interface IPluginMetadata
{
    /// <summary>Best first. Empty when nothing matched, which is an answer rather than a failure.</summary>
    Task<IReadOnlyList<PluginMetadataMatch>> QueryAsync(
        PluginMetadataQuery query,
        CancellationToken ct = default
    );
}
