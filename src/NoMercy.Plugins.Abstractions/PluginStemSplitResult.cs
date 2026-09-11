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
/// What became of <see cref="IPluginAudioTools.SplitStemsAsync" />. A track
/// the host could not open, or a Spleeter model that is not installed on this
/// server, both look like an empty stem list unless the reason travels with
/// the result, so it does here.
/// </summary>
/// <param name="Stems">The separated stems. Empty when refused.</param>
/// <param name="Refusal">Why not, in words the owner can act on. Null when accepted.</param>
public sealed record PluginStemSplitResult(IReadOnlyList<PluginStemFile> Stems, string? Refusal)
{
    public bool Accepted => Refusal is null;

    public static PluginStemSplitResult Refused(string reason) => new([], reason);
}
