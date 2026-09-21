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
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Plugins.Access;

/// <summary>Where one person is told what they may now open.</summary>
public interface IPluginAccessHub
{
    void Send(Guid userId, Ulid pluginId, string access);
}

/// <summary>
/// Tells every account on this server what its answer is now.
/// <para>
/// Everybody, not only whoever changed. A client that missed an earlier
/// message would otherwise stay wrong until somebody reloaded it, and the
/// thing being corrected is a plugin appearing or vanishing on a television
/// nobody is standing in front of.
/// </para>
/// <para>
/// One message per account, never a broadcast. What a person may open is
/// theirs, and sending everyone's answer to everyone would tell a household
/// who paid for what.
/// </para>
/// </summary>
public class PluginAccessNotifier(
    IPluginAccessResolver resolver,
    IPluginMembership membership,
    IPluginManifestSource plugins,
    IPluginAccessHub hub,
    Func<Guid> owner
)
{
    public void AccessMayHaveChanged(Ulid pluginId)
    {
        foreach (Guid userId in membership.EveryoneOn(owner()))
            hub.Send(
                userId,
                pluginId,
                resolver.Resolve(pluginId, userId).ToString().ToLowerInvariant()
            );
    }

    /// <summary>
    /// For the changes that arrive as a list rather than a plugin. A
    /// revocation list names what it blocks, never what it stopped blocking,
    /// so the only honest reading is that every answer may be different.
    /// </summary>
    public void EverythingMayHaveChanged()
    {
        foreach (PluginInfo plugin in plugins.All())
            AccessMayHaveChanged(plugin.Id);
    }
}
