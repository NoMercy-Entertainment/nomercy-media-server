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
/// The kinds of thing an owner can grant a plugin.
/// <para>
/// Consent started as one boolean per plugin, which answers "may this plugin
/// run at all" and nothing else. Everything a plugin needs afterwards has the
/// same shape as that question — a host the manifest could not name at package
/// time, a library it may write to — so it is the same mechanism with a kind
/// and a value rather than a new store per resource.
/// </para>
/// <para>
/// A new kind is a constant here plus an enforcement point. It is deliberately
/// not an enum: a plugin platform that needs a host release to add a grant kind
/// is a platform that will grow bypasses instead.
/// </para>
/// </summary>
public static class PluginGrantKind
{
    /// <summary>
    /// The plugin's declared capability set as a whole — the answer to "may this
    /// plugin run". Carries <see cref="PluginGrant.Everything"/> as its value.
    /// </summary>
    public const string Capability = "capability";

    /// <summary>
    /// One outbound host, granted after install because the manifest could not
    /// know it. The value is a host pattern in the same glob the manifest uses.
    /// </summary>
    public const string NetworkHost = "network.host";

    /// <summary>
    /// One mediated capability, as `capability.&lt;name&gt;`. Built from the
    /// capability rather than listed beside it, so a new one cannot be added and
    /// left ungated by someone who forgot the second list.
    /// </summary>
    public static string ForCapability(string capability) => PluginCapability.GrantFor(capability);

    /// <summary>
    /// Playing something the library does not own, by URL.
    ///
    /// Finer than the player capability as a whole, because arranging a viewer's
    /// own media and choosing what comes out of their speakers are different
    /// amounts of trust, and only the second one needs asking about.
    /// </summary>
    public const string PlayerSource = "player.source";

    /// <summary>
    /// One library the plugin may write to and delete within, by library id.
    /// The value is a library id, or <see cref="PluginGrant.Everything"/> for
    /// every library the server has.
    /// </summary>
    public const string LibraryWrite = "library.write";
}

/// <summary>Values shared across grant kinds.</summary>
public static class PluginGrant
{
    /// <summary>
    /// Every member of a kind. Recorded as a literal so a grant is always a
    /// value the owner agreed to rather than the absence of one — an empty
    /// grant list denies, and it must keep denying.
    /// </summary>
    public const string Everything = "*";
}
