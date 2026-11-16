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

namespace NoMercy.Api.Plugins;

/// <summary>
/// The one refusal every LAN surface gives, whatever was wrong: a caller from
/// outside, a wrong or revoked credential, a method, a route, an address, or
/// a media link that is not this device's. One wording for all of them, so a
/// caller learns nothing about which part of a guess was right.
/// </summary>
public static class PluginLanRefusal
{
    public static PluginRefusal For(Ulid pluginId) =>
        new(
            PluginRefusalCodes.LanCredentialInvalid,
            pluginId.ToString(),
            "The device could not reach the plugin.",
            "This address answers only to a device the owner added on the plugin's page, on the same network as the server.",
            "Use the address from the plugin's page, on a device in the same house as the server. Add the device again if it was removed.",
            PluginRefusalSeverity.Blocked
        );
}
