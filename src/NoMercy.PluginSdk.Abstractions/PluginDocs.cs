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
/// Every docs page a refusal sends a plugin author to, in one place.
/// <para>
/// A refusal is read by someone who is already stuck, so a link that goes
/// nowhere costs more than no link. The pages live in two places: the plugin
/// docs site, and this repository for the pages the site does not carry yet.
/// When a page moves, change it here and every refusal follows.
/// </para>
/// <para>
/// Part of ABI 12.11. Constants on purpose: a constant is copied into the code
/// that reads it, so a plugin built against one never asks the server for it at
/// run time. Adding a page later is a minor addition, like any public constant.
/// </para>
/// </summary>
public static class PluginDocs
{
    /// <summary>The docs site. Capability pages are generated into it from the plugin contract.</summary>
    public const string Site = "https://docs.nomercy.tv";

    /// <summary>The plugin section of the docs site.</summary>
    public const string Root = Site + "/nomercy-plugins";

    /// <summary>The plugin pages kept in this repository until the docs site carries them.</summary>
    public const string Repository =
        "https://github.com/NoMercy-Entertainment/nomercy-media-server/blob/dev/docs/plugins";

    public const string Overview = Root + "/overview";
    public const string Capabilities = Root + "/capabilities";
    public const string RuntimeAndIsolation = Root + "/handbook/runtime-and-isolation";

    public const string BrowserHeadless = Capabilities + "/browser-headless";
    public const string Hub = Capabilities + "/hub";
    public const string MediaLive = Capabilities + "/media-live";
    public const string MediaProxy = Capabilities + "/media-proxy";
    public const string MediaRecord = Capabilities + "/media-record";
    public const string MetadataQuery = Capabilities + "/metadata-query";
    public const string NativeCode = Capabilities + "/native-code";
    public const string NetworkDial = Capabilities + "/network-dial";
    public const string NetworkFetch = Capabilities + "/network-fetch";
    public const string NetworkListen = Capabilities + "/network-listen";
    public const string ProcessSpawn = Capabilities + "/process-spawn";
    public const string Rest = Capabilities + "/rest";
    public const string Scheduler = Capabilities + "/scheduler";
    public const string Secrets = Capabilities + "/secrets";
    public const string Settings = Capabilities + "/settings";
    public const string StoragePath = Capabilities + "/storage-path";
    public const string UiMount = Capabilities + "/ui-mount";
    public const string UserIdentity = Capabilities + "/user-identity";

    public const string Migration = Repository + "/migration.md";
    public const string Versioning = Repository + "/versioning.md";
    public const string Manifest = Repository + "/manifest.md";
    public const string ManifestDependencies = Manifest + "#dependencies";
    public const string ManifestTranslations = Manifest + "#translations";
    public const string LanRoutes = Repository + "/lan-routes.md";

    /// <summary>
    /// The page for a capability, from the path the contract generator gave it
    /// (<see cref="PluginCapabilityDescriptor.DocsUrl" />). The list of every
    /// capability when there is no descriptor.
    /// <para>
    /// Internal, unlike the constants: a method is a member a plugin would call
    /// at run time, and a server built before it would not have it.
    /// </para>
    /// </summary>
    internal static string ForCapability(PluginCapabilityDescriptor? descriptor)
    {
        return descriptor is null ? Capabilities : Site + descriptor.DocsUrl;
    }
}
