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
/// How a <see cref="PluginActionType.CallPlugin"/> intent reaches the plugin.
/// REST for a request that has an answer, the hub for anything the plugin will
/// keep reporting on.
/// </summary>
public static class PluginActionTransport
{
    public const string Rest = "rest";
    public const string Hub = "hub";
}
