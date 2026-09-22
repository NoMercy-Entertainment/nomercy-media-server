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

namespace NoMercy.PluginSdk.Capabilities;

public interface IPluginConsentService
{
    bool IsBaseline(PluginCapabilities? capabilities);
    bool HasConsent(Ulid pluginId);

    /// <summary>
    /// Whether a recorded consent still covers what <paramref name="capabilities"/>
    /// asks for. False when there is no consent at all, or when the manifest
    /// has widened past what the owner approved.
    /// <para>
    /// An id-only record from before capabilities were tracked is upgraded
    /// here, seeded from the installed
    /// manifest this is being asked about, which is why the installed version
    /// is a parameter. Design section 9: every capability the manifest declared
    /// is consented at the version that was installed.
    /// </para>
    /// </summary>
    bool ConsentCoversCapabilities(
        Ulid pluginId,
        PluginCapabilities? capabilities,
        Version installedVersion
    );

    /// <summary>
    /// What the owner approved, as the consent record holds it. Null when
    /// there is no consent yet, so a client can tell "never approved" from
    /// "approved a smaller set" and name the difference to the owner.
    /// </summary>
    PluginCapabilities? ConsentedCapabilities(Ulid pluginId);

    void GrantConsent(Ulid pluginId, PluginCapabilities? capabilities, Version manifestVersion);
    void RevokeConsent(Ulid pluginId);

    /// <summary>
    /// Records the owner saying yes to one capability, at the version that
    /// asked for it.
    /// <para>
    /// Per capability because the only answers used to be everything or
    /// nothing, and nothing meant the plugin did not run. An owner who wants a
    /// radio plugin to reach the internet and not to spawn processes had no way
    /// to say so.
    /// </para>
    /// </summary>
    void ApproveCapability(Ulid pluginId, string capability, Version manifestVersion);

    /// <summary>Takes one back, leaving the rest as they were.</summary>
    void RevokeCapability(Ulid pluginId, string capability);

    bool IsApproved(Ulid pluginId, string capability);

    /// <summary>The version that asked, or null when it was never approved.</summary>
    Version? ApprovedAt(Ulid pluginId, string capability);
}
