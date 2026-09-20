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

using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Media;

/// <summary>
/// What <c>context.Media</c> is on this host.
/// <para>
/// Proxying is here. The other four are not built yet, and each says so by
/// name rather than being null: a plugin that reaches for one gets a sentence
/// saying this host does not carry it, which is a thing an author can act on.
/// </para>
/// </summary>
public class PluginMedia(Ulid pluginId, IPluginMediaProxy proxy) : IPluginMedia
{
    public IPluginMediaProxy Proxy => proxy;

    public IPluginMediaTranscode Transcode => throw NotHere("IPluginContext.Media.Transcode");

    public IPluginMediaRemux Remux => throw NotHere("IPluginContext.Media.Remux");

    public IPluginMediaLive Live => throw NotHere("IPluginContext.Media.Live");

    public IPluginRecorder Record => throw NotHere("IPluginContext.Media.Record");

    private PluginRefusedException NotHere(string facade) =>
        new(PluginRefusalMessages.FacadeNotOnThisHost(pluginId.ToString(), facade));
}
