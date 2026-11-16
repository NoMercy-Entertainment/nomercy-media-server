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

namespace NoMercy.Data.Plugins;

/// <summary>The engine names <see cref="TrackStem.Engine" /> holds.</summary>
public static class StemEngineNames
{
    public const string Spleeter = "spleeter";
    public const string Roformer = "roformer";

    public static string Of(StemsplitEngine engine) =>
        engine == StemsplitEngine.Roformer ? Roformer : Spleeter;

    /// <summary>
    /// The register's own spelling of a name a producer wrote, ignoring case
    /// and surrounding blanks; null for a name that is neither engine.
    /// </summary>
    public static string? Known(string engine) =>
        engine.Trim().ToLowerInvariant() switch
        {
            Spleeter => Spleeter,
            Roformer => Roformer,
            _ => null,
        };

    /// <summary>
    /// The engine a producer version names, in the shape the stems job writes
    /// (<c>spleeter-2stems-f16@…</c>, <c>roformer-full@…</c>); null for any
    /// other.
    /// </summary>
    public static string? FromProducerVersion(string producerVersion)
    {
        if (producerVersion.StartsWith(Spleeter + "-", StringComparison.OrdinalIgnoreCase))
            return Spleeter;

        if (producerVersion.StartsWith(Roformer + "-", StringComparison.OrdinalIgnoreCase))
            return Roformer;

        return null;
    }
}
