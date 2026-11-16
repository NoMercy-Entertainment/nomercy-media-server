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
/// The server's own note, set as a request feature, that this request is a
/// LAN device the server has already checked: the network, the credential,
/// and the route or ticket it asked for.
/// <para>
/// A server type a plugin does not reference, so only the server can set it.
/// What the plugin reads is the <c>PluginLanCaller</c> copy; what the server
/// trusts is this.
/// </para>
/// </summary>
public sealed record PluginLanDispatch(Ulid PluginId, string DeviceId, string Name);
