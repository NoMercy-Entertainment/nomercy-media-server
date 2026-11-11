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
    /// The same run with <paramref name="options" />: today, a longer timeout
    /// for a graph that runs whisper over a whole track.
    /// <para>
    /// Default-implemented so an implementer written against an older ABI
    /// keeps compiling; that fallback runs with the host's default options.
    /// Added in ABI 12.6.
    /// </para>
    /// </summary>
    Task<PluginAudioRunResult> RunFilterGraphAsync(
        PluginAudioInput input,
        PluginFilterGraph graph,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions options,
        CancellationToken ct = default
    ) => RunFilterGraphAsync(input, graph, onStdOut, onStdErr, ct);

    /// <summary>
    /// Runs one complex filter graph over one or more inputs and encodes its
    /// labelled output pads to files in the plugin's own data folder.
    /// <para>
    /// This is how a plugin keeps audio it derived - a normalised copy, a
    /// preview clip, stems from another model - and how two sources meet in
    /// one graph without the plugin ever being given a library path. The host
    /// writes the whole command line; the plugin supplies the graph and names
    /// the pads. A finished file can go straight to
    /// <c>Media.Proxy.MintFileAsync</c>.
    /// </para>
    /// <para>
    /// Each file is written under a temporary name and takes its real name
    /// only once ffmpeg has exited 0, all of them together, so a run that
    /// failed, timed out or was cancelled leaves nothing behind and a file
    /// that was already there stays as it was. The files count against the
    /// plugin's disk quota, and a run that would go past it is ended and
    /// refused. One run per plugin at a time, shared with
    /// <see cref="RunFilterGraphAsync(PluginAudioInput, PluginFilterGraph, Action{string}?, Action{string}?, CancellationToken)" />
    /// and <see cref="SplitStemsAsync" />.
    /// </para>
    /// <para>
    /// stdout carries ffmpeg's progress report next to whatever the graph
    /// prints: <c>key=value</c> lines every five seconds, closed by
    /// <c>progress=end</c>, which the host reads as the files being complete:
    /// a graph must not print that line itself. Pass
    /// <see cref="PluginAudioRunOptions.StallTimeout" /> for a run that may
    /// take longer than sixty minutes.
    /// </para>
    /// <para>
    /// Default-implemented as a refusal so an implementer written against an
    /// older ABI keeps compiling. Added in ABI 12.7.
    /// </para>
    /// </summary>
    Task<PluginAudioRunResult> RenderAsync(
        PluginAudioRender render,
        Action<string>? onStdOut,
        Action<string>? onStdErr,
        PluginAudioRunOptions options,
        CancellationToken ct = default
    ) => Task.FromResult(PluginAudioRunResult.Refused("this host cannot render audio to a file"));

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
