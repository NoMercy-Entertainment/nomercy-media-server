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

namespace NoMercy.PluginSdk;

/// <summary>
/// Host-side options for plugin assembly loading. <see cref="SharedAssemblies"/>
/// are the assemblies whose type identity must be preserved across the
/// host/plugin boundary: a plugin's <see cref="PluginLoadContext"/> defers to
/// the default load context for these so types remain castable. Bind this from
/// configuration to add a shared framework package without a code change.
/// </summary>
public record PluginHostOptions
{
    /// <summary>
    /// Extra assemblies to share, added to <see cref="DefaultSharedAssemblies"/>
    /// rather than replacing them.
    /// <para>
    /// This is the bindable one, and it is a <see cref="List{T}"/> for that
    /// reason: configuration binding cannot populate an
    /// <see cref="IReadOnlySet{T}"/>, so the property that documented itself as
    /// bindable could never actually have been bound. Additive because a
    /// deployment adding one package should not have to restate the six the
    /// platform requires, and would break the boundary if it forgot one.
    /// </para>
    /// </summary>
    public List<string> AdditionalSharedAssemblies { get; init; } = [];

    private readonly IReadOnlySet<string>? _sharedAssemblies;

    /// <summary>
    /// Everything whose type identity crosses the host/plugin boundary: the
    /// built-in set plus whatever was configured.
    /// </summary>
    public IReadOnlySet<string> SharedAssemblies
    {
        get =>
            _sharedAssemblies
            ?? new HashSet<string>(
                DefaultSharedAssemblies.Concat(AdditionalSharedAssemblies),
                StringComparer.OrdinalIgnoreCase
            );
        init => _sharedAssemblies = value;
    }

    /// <summary>The built-in shared-assembly set used when none is configured.</summary>
    public static IReadOnlySet<string> DefaultSharedAssemblies { get; } =
        new HashSet<string>
        {
            "NoMercy.PluginSdk.Abstractions",
            "NoMercy.PluginSdk.Mvc",
            "NoMercy.Events",
            // The design system's contract. A plugin builds its screens from the
            // same component records the host renders, so the two must be the
            // same types and not two copies that merely look alike.
            "NoMercy.Design",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.DependencyInjection",
            // A plugin annotating its own DTOs with [JsonProperty] gets those
            // attributes from its own copy of Newtonsoft otherwise, and the
            // host's formatter does not recognise attributes from a different
            // assembly identity — the response then silently ships camelCase
            // where every client expects snake_case.
            "Newtonsoft.Json",
            // A plugin's id crosses the boundary in both directions - the host
            // asks a plugin who it is, and hands it back on every view request.
            "Ulid",
        };
}
