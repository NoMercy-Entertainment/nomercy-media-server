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

using System.Text.Json.Serialization;

namespace NoMercy.PluginSdk.Abstractions;

/// <summary>
/// The few routes a plugin serves to software on the owner's network that
/// cannot sign in, such as a television tuner client.
/// <para>
/// Named one by one in the manifest, where the owner reads them before
/// consenting, rather than opened per assembly the way <c>restAnonymous</c>
/// opens every route. A device reaches them only with a credential the owner
/// created for it on the plugin's page, only from a private network, and only
/// with <c>GET</c>. Added in ABI 12.10.
/// </para>
/// </summary>
public class PluginLanCapability
{
    /// <summary>
    /// Route templates relative to the plugin's own prefix, as the plugin's
    /// controllers write them: <c>hdhr/discover.json</c>, or
    /// <c>hdhr/auto/{channel}</c> for a value in one segment. See
    /// <see cref="PluginLanRoutes" /> for what a template may hold.
    /// </summary>
    [JsonPropertyName("routes")]
    public List<string> Routes { get; init; } = [];
}
