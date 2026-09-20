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

namespace NoMercy.Plugins.Abstractions;

/// <summary>
/// A question for the metadata providers the server is already configured with.
/// <para>
/// The plugin asks the server rather than a provider directly, so it needs no
/// API key of its own and the owner's rate limit is spent once. A plugin
/// holding its own key also kept working after the owner revoked the
/// capability, which is the part that mattered.
/// </para>
/// </summary>
public sealed record PluginMetadataQuery
{
    /// <summary>
    /// A provider the manifest named, or <c>*</c> to ask every provider the
    /// owner configured and take the best answer.
    /// </summary>
    public required string Provider { get; init; }

    public required PluginMediaKind Kind { get; init; }

    public required string Title { get; init; }

    public int? Year { get; init; }

    /// <summary>An external id the plugin already holds, which beats a title every time.</summary>
    public string? ExternalId { get; init; }

    public string Language { get; init; } = "en";
}
