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

using Newtonsoft.Json;
using NoMercy.PluginSdk.Provenance;

namespace NoMercy.Api.DTOs.Dashboard;

/// <summary>
/// Where an installed plugin came from. Every field is always sent, null when
/// it is not known, so a client reads one shape whatever the kind.
/// See docs/plugins/plugin-info.md.
/// </summary>
public class PluginInstallSourceDto
{
    /// <summary><c>repository</c>, <c>file</c> or <c>unknown</c>.</summary>
    [JsonProperty("kind")]
    public string Kind { get; init; } = PluginInstallSourceKind.Unknown;

    /// <summary>The repository the package was downloaded from. Null unless the kind is <c>repository</c>.</summary>
    [JsonProperty("repository")]
    public PluginInstallRepositoryDto? Repository { get; init; }

    /// <summary>Whether the official NoMercy marketplace listed the entry. Null when unknown.</summary>
    [JsonProperty("official_marketplace")]
    public bool? OfficialMarketplace { get; init; }

    /// <summary>Whether the checksum the repository published matched the package. Null when unknown.</summary>
    [JsonProperty("checksum_verified")]
    public bool? ChecksumVerified { get; init; }

    /// <summary>The version that install put in place. Null when unknown.</summary>
    [JsonProperty("version")]
    public string? Version { get; init; }

    /// <summary>When the install was recorded, UTC. Null when unknown.</summary>
    [JsonProperty("recorded_at")]
    public DateTime? RecordedAt { get; init; }

    public static PluginInstallSourceDto From(PluginProvenance provenance) =>
        new()
        {
            Kind = provenance.Kind,
            Repository = provenance.RepositoryUrl is null
                ? null
                : new()
                {
                    Name = provenance.RepositoryName,
                    Url = provenance.RepositoryUrl,
                    Trusted = provenance.RepositoryTrusted ?? false,
                    Removed = provenance.RepositoryRemoved ?? false,
                },
            OfficialMarketplace = provenance.FromOfficialMarketplace,
            ChecksumVerified = provenance.ChecksumVerified,
            Version = provenance.Version,
            RecordedAt = provenance.RecordedAt,
        };
}
