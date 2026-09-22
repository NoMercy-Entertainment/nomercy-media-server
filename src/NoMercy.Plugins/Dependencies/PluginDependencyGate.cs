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
using NoMercy.PluginSdk.Capabilities;
using NoMercy.PluginSdk.Entitlements;

namespace NoMercy.PluginSdk.Dependencies;

/// <summary>
/// A plugin that leans on another runs only while that other one runs.
/// <para>
/// Nothing is ever bought on the owner's behalf. A paid dependency nobody
/// owns leaves the dependent installed and off, with a message naming what to
/// buy and where, rather than a charge nobody agreed to.
/// </para>
/// </summary>
public class PluginDependencyGate(
    IPluginManifestSource plugins,
    IPluginEntitlementStore entitlements,
    TimeProvider clock,
    Func<Guid> owner
)
{
    public PluginRefusal? Check(Ulid pluginId) =>
        plugins.Find(pluginId) is { } dependent ? Check(dependent) : null;

    public PluginRefusal? Check(PluginInfo dependent)
    {
        foreach (PluginDependency dependency in dependent.Dependencies)
        {
            if (Refuse(dependent, dependency) is { } refusal)
                return refusal;
        }

        return null;
    }

    private PluginRefusal? Refuse(PluginInfo dependent, PluginDependency dependency)
    {
        Ulid id = dependency.Id.Value;
        PluginInfo? installed = plugins.Find(id);

        if (installed is null || !PluginSemver.Satisfies(installed.Version, dependency.Range))
            return Missing(dependent, dependency);

        // Checked before the entitlement, because a free plugin leaning on a
        // paid one is the publisher's mistake and telling the owner to go and
        // buy something would make it theirs.
        if (dependent.Tier == PluginTier.Free && installed.Tier == PluginTier.Paid)
            return TierMismatch(dependent, dependency);

        if (installed.Tier == PluginTier.Paid && !Holds(id))
            return Unpaid(dependent, dependency);

        if (installed.Status != PluginStatus.Active)
            return Paused(dependent, dependency);

        return null;
    }

    private bool Holds(Ulid pluginId) =>
        entitlements.Current.For(pluginId, owner(), clock.GetUtcNow()) is not null;

    private static string Named(PluginInfo dependent) => $"{dependent.Name} {dependent.Version}";

    private static PluginRefusal Missing(PluginInfo dependent, PluginDependency dependency) =>
        new(
            PluginRefusalCodes.DependencyMissing,
            Named(dependent),
            $"The plugin did not start, because it needs {dependency.Id} {dependency.Range}.",
            "That plugin is not installed on this server, or the version that is installed falls outside the range this one needs.",
            $"Install {dependency.Id} from the marketplace, or update it to a version inside {dependency.Range}.",
            PluginRefusalSeverity.Blocked
        );

    private static PluginRefusal Unpaid(PluginInfo dependent, PluginDependency dependency) =>
        new(
            PluginRefusalCodes.DependencyPaidNotOwned,
            Named(dependent),
            $"The plugin is installed and off, because it needs {dependency.Id}.",
            "That dependency is a paid plugin and the owner of this server holds no entitlement for it. NoMercy never buys a dependency for you.",
            $"Buy {dependency.Id} on nomercy.tv with the account that owns this server, then turn this plugin on.",
            PluginRefusalSeverity.Blocked
        );

    private static PluginRefusal Paused(PluginInfo dependent, PluginDependency dependency) =>
        new(
            PluginRefusalCodes.DependencyPaused,
            Named(dependent),
            $"The plugin paused, because {dependency.Id} is not running.",
            "A plugin it depends on was turned off, revoked or removed.",
            $"Turn {dependency.Id} back on, or update it if it was revoked.",
            PluginRefusalSeverity.Blocked
        );

    private static PluginRefusal TierMismatch(PluginInfo dependent, PluginDependency dependency) =>
        new(
            PluginRefusalCodes.DependencyTierMismatch,
            Named(dependent),
            $"The plugin did not start, because it depends on {dependency.Id}.",
            "A free plugin may depend only on free plugins, so installing a free one never pushes anybody into a purchase.",
            "The publisher has to drop the paid dependency or publish this plugin as paid. There is nothing to change on this server.",
            PluginRefusalSeverity.Blocked
        );
}
