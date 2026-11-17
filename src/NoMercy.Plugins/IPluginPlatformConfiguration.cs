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

namespace NoMercy.PluginSdk;

/// <summary>
/// The one platform config file, <c>plugins/data/platform/config.json</c>,
/// that consent, grants, approvals and install sources all write.
/// <para>
/// A named type so the container hands every store the same instance. A save
/// reads the file, merges its own key and writes it back under the
/// instance's lock, so two instances over one file could each write back a
/// copy without the other's change and lose it. Every store changes its
/// record through <see cref="IPluginAtomicConfiguration.Update{T}" />, so two
/// changes to the same record cannot lose one either.
/// </para>
/// </summary>
public interface IPluginPlatformConfiguration : IPluginAtomicConfiguration;
