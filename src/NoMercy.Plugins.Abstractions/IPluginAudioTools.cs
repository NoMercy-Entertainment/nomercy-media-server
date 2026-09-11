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
/// Running the server's own ffmpeg build - the one built with beat, key and
/// stem detection, never with rubberband - over a track or a derived file.
/// <para>
/// Without this, a plugin doing DJ-grade analysis has two bad options: ship
/// its own ffmpeg, which drifts from the server's build and cannot use its
/// custom filters, or shell out to a binary it found on the host's PATH,
/// which is a different binary on every self-hosted machine and answers to
/// no capability check. Neither is a thing an owner can consent to or revoke.
/// </para>
/// <para>
/// Elevated - see <see cref="PluginHookCapability.AudioTools" /> - because an
/// arbitrary filter graph is an arbitrary ffmpeg invocation against a library
/// file, which is not a thing to arrive through a baseline auto-enable.
/// </para>
/// </summary>
public interface IPluginAudioTools
{
    /// <summary>
    /// Runs one filter graph over one input and streams its own stdout and
    /// stderr back live, so a caller can show progress or bail out through
    /// <paramref name="ct" /> rather than waiting for a multi-minute stem
    /// split to finish before learning it failed on the first second.
    /// </summary>
    Task<PluginAudioRunResult> RunFilterGraphAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        CancellationToken ct = default
    );

    /// <summary>
    /// Coverage Full splits the whole track; MixIn or MixOut split that
    /// window (first 20 % / last 25 %, computed by the host from the track
    /// duration). Stems land in the derived store and in the stem register.
    /// </summary>
    Task<PluginStemSplitResult> SplitStemsAsync(
        string trackId,
        PluginStemCoverage coverage,
        PluginStemSet stemSet,
        CancellationToken ct = default
    );
}
