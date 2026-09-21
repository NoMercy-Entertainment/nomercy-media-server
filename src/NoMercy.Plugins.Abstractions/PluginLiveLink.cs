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
namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// One upstream for a live channel, as the host holds it.
///
/// <para>
/// The host's own ordered view, not the plugin's: a plugin supplies
/// <see cref="PluginProxyLink" />, whose resolver runs on the server and whose
/// answer never leaves it. This record carries no resolver and no credential,
/// and it is never part of what a client receives.
/// </para>
///
/// <para>
/// Priority is why a provider that drops does not end playback: the next link
/// is tried in its place. Before that, one provider refusing a second
/// connection ended the stream for a viewer who had simply opened the guide.
/// </para>
/// </summary>
public sealed record PluginLiveLink(
    int Priority,
    Uri Url,
    string Container,
    string? Referer,
    string? UserAgent,
    int? MaxConnections
);
