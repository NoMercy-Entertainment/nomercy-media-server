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

/// <summary>
/// Whether a plugin starts running on its own, or waits for the owner.
/// <para>
/// Its own type because it is the whole security decision of a load, and it sat
/// inline in the loader where nothing could test it. A verification that came
/// back Trusted used to be enough on its own here, so a plugin from an index
/// the owner trusts — and, because a matching checksum grants trust too, any
/// plugin whose repository published one — started reaching the network and the
/// claims pipeline without the owner ever being asked.
/// </para>
/// <para>
/// Trust says where a plugin came from. From Phase 3 it skips the marketplace
/// review hold, which is a delay before a release is published, and never the
/// owner's own decision about their own machine.
/// </para>
/// </summary>
public static class PluginAutoEnable
{
    public static bool Allows(PluginManifest manifest, IPluginConsentService consentService)
    {
        if (!manifest.AutoEnabled)
            return false;

        return consentService.IsBaseline(manifest.Capabilities)
            || consentService.ConsentCoversCapabilities(
                manifest.Id.Value,
                manifest.Capabilities,
                InstalledVersion(manifest)
            );
    }

    /// <summary>
    /// Whether the owner already said yes to a smaller request than this
    /// manifest now makes, and so has to be asked again.
    /// </summary>
    public static bool NeedsReConsent(PluginManifest manifest, IPluginConsentService consentService)
    {
        if (consentService.IsBaseline(manifest.Capabilities))
            return false;

        return consentService.HasConsent(manifest.Id.Value)
            && !consentService.ConsentCoversCapabilities(
                manifest.Id.Value,
                manifest.Capabilities,
                InstalledVersion(manifest)
            );
    }

    /// <summary>
    /// The version a consent record from before capabilities were tracked is
    /// upgraded at. A manifest version the platform cannot read is recorded as
    /// 0.0 rather than refused: what was consented is the capability set, and
    /// the version is the label on it.
    /// </summary>
    private static Version InstalledVersion(PluginManifest manifest) =>
        Version.TryParse(manifest.Version, out Version? parsed) ? parsed : new Version(0, 0);
}
