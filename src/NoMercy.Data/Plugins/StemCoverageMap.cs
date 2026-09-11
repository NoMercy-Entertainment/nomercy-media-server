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

using NoMercy.Database.Models.Music;
using NoMercy.Plugins.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The one place the plugin's stem coverage and the database's own are
/// translated into each other - the reader and the writer used to carry a
/// private copy each, which is exactly how two mappings drift apart.
/// <para>
/// Explicit switches rather than a numeric cast: the two enums happen to
/// share values today, and a cast would silently keep "happening to" work if
/// that ever stopped being true.
/// </para>
/// </summary>
public static class StemCoverageMap
{
    public static StemCoverage ToDb(PluginStemCoverage coverage) =>
        coverage switch
        {
            PluginStemCoverage.Full => StemCoverage.Full,
            PluginStemCoverage.MixIn => StemCoverage.MixIn,
            PluginStemCoverage.MixOut => StemCoverage.MixOut,
            _ => throw new ArgumentOutOfRangeException(
                nameof(coverage),
                coverage,
                "Unknown stem coverage"
            ),
        };

    public static PluginStemCoverage ToPlugin(StemCoverage coverage) =>
        coverage switch
        {
            StemCoverage.Full => PluginStemCoverage.Full,
            StemCoverage.MixIn => PluginStemCoverage.MixIn,
            StemCoverage.MixOut => PluginStemCoverage.MixOut,
            _ => throw new ArgumentOutOfRangeException(
                nameof(coverage),
                coverage,
                "Unknown stem coverage"
            ),
        };
}
