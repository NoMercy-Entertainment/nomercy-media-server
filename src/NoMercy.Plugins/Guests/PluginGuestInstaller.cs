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

namespace NoMercy.Plugins.Guests;

/// <summary>
/// A guest brings a plugin onto somebody else's machine.
/// <para>
/// It may only hold capabilities whose effects leave with them. The
/// vocabulary marks each one as reversible or not and this decides nothing on
/// its own: a capability added later is judged by what the contract says about
/// it, not by a list here that somebody has to remember to update.
/// </para>
/// </summary>
public class PluginGuestInstaller(IPluginGuestInstallStore store, IPluginDataPurge purge)
{
    public PluginRefusal? Install(PluginManifest manifest, Guid guestId)
    {
        string? offending = (manifest.Capabilities?.Hooks ?? []).FirstOrDefault(NotReversible);

        if (offending is not null)
            return new(
                PluginRefusalCodes.GuestCapabilityIrreversible,
                $"{manifest.Name} {manifest.Version}",
                $"The server did not install the plugin, because it asks for {offending}.",
                $"A guest's plugin may only hold capabilities the server can undo when the guest leaves, and {offending} cannot be undone.",
                "Ask the owner of this server to install it themselves, or use a plugin that stays inside the capabilities a guest may hold.",
                PluginRefusalSeverity.Blocked
            );

        store.Record(manifest.Id.Value, guestId);

        return null;
    }

    /// <summary>
    /// Everything that was this guest's, gone with them. The ids come back so
    /// the caller can unload what was running.
    /// </summary>
    public async Task<IReadOnlyList<Ulid>> PurgeForAsync(
        Guid guestId,
        CancellationToken ct = default
    )
    {
        IReadOnlyList<Ulid> removed = store.PluginsFor(guestId);

        foreach (Ulid pluginId in removed)
        {
            await purge.PurgeAsync(pluginId, ct);
            store.Forget(pluginId);
        }

        return removed;
    }

    /// <summary>
    /// A name the vocabulary does not carry counts as not reversible. Nothing
    /// could have declared it, and guessing in a guest's favour is guessing
    /// about somebody else's machine.
    /// </summary>
    private static bool NotReversible(string capability) =>
        PluginCapabilityVocabulary.ByName(capability) is not { Reversible: true };
}
