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

using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Plugins.Abstractions;
using NoMercy.Plugins.Capabilities;

namespace NoMercy.Api.Plugins;

/// <summary>
/// What a plugin asks for and the owner's answer to each, built once.
/// <para>
/// Two routes show this list. Built in two places they would drift, and an
/// owner comparing the consent page with the permissions page would find two
/// different accounts of the same plugin.
/// </para>
/// </summary>
public class PluginCapabilityStates(IPluginConsentService consent)
{
    public IReadOnlyList<PluginCapabilityStateDto> For(PluginInfo plugin) =>
        [.. Declared(plugin).Select(descriptor => Describe(plugin.Id, descriptor))];

    /// <summary>
    /// The capabilities this plugin's manifest declares, as the vocabulary
    /// describes them. A hook the vocabulary does not carry is skipped rather
    /// than shown: the owner cannot meaningfully answer for something the
    /// server has no description of.
    /// </summary>
    public static IEnumerable<PluginCapabilityDescriptor> Declared(PluginInfo plugin) =>
        (plugin.Capabilities?.Hooks ?? [])
            .Select(PluginCapabilityVocabulary.ByName)
            .Where(descriptor => descriptor is not null)
            .Select(descriptor => descriptor!);

    private PluginCapabilityStateDto Describe(Ulid id, PluginCapabilityDescriptor descriptor) =>
        new()
        {
            Name = descriptor.Name,
            SummaryKey = descriptor.Summary,
            Trust = descriptor.Trust.ToString().ToLowerInvariant(),
            DocsUrl = descriptor.DocsUrl,
            Approved = consent.IsApproved(id, descriptor.Name),
            ApprovedAtVersion = consent.ApprovedAt(id, descriptor.Name)?.ToString(),
        };
}
