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
/// One answer from a metadata provider.
/// <para>
/// The confidence is carried rather than hidden, because the server cannot know
/// what a plugin will do with a weak match. A plugin filing a recording may
/// accept a poor one; a plugin renaming the owner's files should not.
/// </para>
/// </summary>
/// <param name="Provider">Which provider answered, since a query may ask them all.</param>
/// <param name="Confidence">Zero to one.</param>
public sealed record PluginMetadataMatch(
    string Provider,
    string ExternalId,
    string Title,
    int? Year,
    PluginMediaKind Kind,
    double Confidence,
    string? Overview,
    Uri? Poster
);
