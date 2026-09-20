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

using Microsoft.Extensions.Logging;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Plugins.Capabilities;

/// <inheritdoc />
public sealed class PluginCapabilityBroker(
    IPluginManifestSource manifests,
    IPluginConsentService consent,
    IPluginGrantStore grants,
    IPluginRefusalCounter counter,
    ILogger<PluginCapabilityBroker> logger
) : IPluginCapabilityBroker
{
    public PluginRefusal? Check(Ulid pluginId, string capability, string? scopeValue = null)
    {
        PluginInfo? info = manifests.Find(pluginId);
        string who = info is null ? pluginId.ToString() : $"{info.Name} {info.Version}";

        // A name outside the vocabulary is a bug in the host, not in the
        // plugin: nothing could have declared it, so refusing the plugin would
        // send its author looking for a line to add that does not exist.
        if (PluginCapabilityVocabulary.ByName(capability) is null)
        {
            logger.LogError(
                "Plugin {PluginId} was checked against '{Capability}', which is not in the vocabulary.",
                pluginId,
                capability
            );
            return null;
        }

        if (info is null)
            return Refuse(
                pluginId,
                PluginRefusalMessages.CapabilityNotDeclared(
                    who,
                    capability,
                    $"An unknown plugin used {capability}."
                )
            );

        if (!Declares(info.Capabilities, capability, scopeValue, out bool scopeDeclared))
            return Refuse(
                pluginId,
                PluginRefusalMessages.CapabilityNotDeclared(
                    who,
                    capability,
                    $"The plugin used {capability}, which its manifest does not declare."
                )
            );

        // This capability, not the plugin. An owner who approved the network
        // and refused process spawning gets exactly that, and a plugin that
        // later asks for more finds the new one pending rather than inheriting
        // the earlier yes.
        //
        // The blanket check still runs first, so a plugin approved before
        // consent was tracked per capability keeps working rather than being
        // refused for answers nobody was ever asked.
        if (
            !consent.IsApproved(pluginId, capability)
            && !consent.ConsentCoversCapabilities(pluginId, info.Capabilities, info.Version)
        )
            return Refuse(pluginId, PluginRefusalMessages.CapabilityNotConsented(who, capability));

        // The scope is checked last so a value the owner granted after install
        // is enough on its own, without the manifest having named it.
        if (
            scopeValue is not null
            && !scopeDeclared
            && !grants.Holds(pluginId, PluginGrantKind.ForCapability(capability), scopeValue)
        )
            return Refuse(
                pluginId,
                PluginRefusalMessages.CapabilityScopeRefused(who, capability, scopeValue)
            );

        return null;
    }

    private PluginRefusal Refuse(Ulid pluginId, PluginRefusal refusal)
    {
        counter.Count(pluginId, refusal);
        return refusal;
    }

    private static bool Declares(
        PluginCapabilities? capabilities,
        string capability,
        string? scopeValue,
        out bool scopeDeclared
    )
    {
        scopeDeclared = false;

        if (capabilities is null)
            return false;

        if (!PluginCapabilityGuard.DeclaresHook(capabilities, capability))
            return false;

        if (scopeValue is null)
        {
            scopeDeclared = true;
            return true;
        }

        scopeDeclared = PluginScopeGlob.AnyMatches(ScopesFor(capabilities, capability), scopeValue);

        return true;
    }

    /// <summary>
    /// The values the manifest named for this capability. Network is the only
    /// one with a shape of its own today; everything else declares the
    /// capability and asks the owner for each value.
    /// </summary>
    private static IReadOnlyList<string> ScopesFor(
        PluginCapabilities capabilities,
        string capability
    ) =>
        capability.StartsWith("network.", StringComparison.Ordinal)
            ? capabilities.Network?.Hosts ?? []
            : [];
}
