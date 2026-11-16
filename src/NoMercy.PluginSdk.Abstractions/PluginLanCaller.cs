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
/// The device on the owner's network that a LAN route is answering, in place
/// of a user.
/// <para>
/// A device, never a person: a LAN request carries no account, and the
/// server strips any token or cookie from it before the plugin sees it. The
/// server has already checked the network, the credential and the route by
/// the time a plugin reads this. Added in ABI 12.10.
/// </para>
/// </summary>
/// <param name="DeviceId">The device's id on this plugin, stable until the owner revokes it.</param>
/// <param name="Name">What the owner called the device, such as "Living room TV".</param>
/// <param name="BaseAddress">
/// The address this device reaches the plugin's LAN routes under, ending in
/// a slash: append a route from the manifest to it, as in
/// <c>new Uri(BaseAddress, "hdhr/lineup.json")</c>. Software such as an
/// HDHomeRun client wants absolute addresses in what it reads. It carries the
/// device's credential, so put it only in answers to this device and never in
/// a log.
/// </param>
public sealed record PluginLanCaller(string DeviceId, string Name, Uri BaseAddress);
