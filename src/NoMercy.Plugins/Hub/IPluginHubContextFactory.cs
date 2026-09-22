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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.PluginSdk.Hub;

/// <summary>
/// Builds the push channel a plugin gets, scoped to its own id.
/// <para>
/// An interface here and the implementation in the API project, because the
/// hub type lives with the other hubs and this assembly is referenced by it,
/// not the other way round.
/// </para>
/// </summary>
public interface IPluginHubContextFactory
{
    IPluginHubContext For(Ulid pluginId);
}
