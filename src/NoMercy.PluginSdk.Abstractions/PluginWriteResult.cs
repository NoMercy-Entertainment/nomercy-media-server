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
/// What became of one write through <see cref="IPluginLyrics" />.
/// A write that never lands - a track id the host does not know, lyrics the
/// host may not replace - looks identical to success unless the
/// reason travels with the result, so it does here rather than as an
/// exception a caller has to know to catch.
/// </summary>
/// <param name="Refusal">Why not, in words the owner can act on. Null when accepted.</param>
public sealed record PluginWriteResult(string? Refusal)
{
    public bool Ok => Refusal is null;

    public static PluginWriteResult Accepted() => new((string?)null);

    public static PluginWriteResult Refused(string reason) => new(reason);
}
