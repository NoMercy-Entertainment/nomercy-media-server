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
/// What became of <see cref="IPluginAudioTools.RunFilterGraphAsync" />. A run
/// that never started - a bad filter graph, a missing model file, a track
/// the host would not open - looks identical to a crash from the outside
/// unless the reason travels with the result, so it does here rather than as
/// an exception a caller has to know to catch.
/// </summary>
/// <param name="ExitCode">ffmpeg's own exit code. -1 when the run never started; see <see cref="Refused" />.</param>
/// <param name="Refusal">Why not, in words the owner can act on. Null when the run started.</param>
public sealed record PluginAudioRunResult(int ExitCode, string? Refusal)
{
    /// <summary>True once the run started, whatever ffmpeg's own exit code turned out to be.</summary>
    public bool Ran => Refusal is null;

    public static PluginAudioRunResult Refused(string reason) => new(-1, reason);
}
