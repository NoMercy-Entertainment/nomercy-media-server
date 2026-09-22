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
/// What the provider allows. A provider that permits two connections and gets
/// three locks the account out, so the host shares one upstream between viewers
/// rather than opening a connection per viewer.
/// </summary>
public sealed record PluginProviderLimits
{
    public int? MaxConnections { get; init; }
    public bool ShareOneUpstream { get; init; }
}
