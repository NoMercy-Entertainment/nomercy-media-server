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
/// How one run of <see cref="IPluginAudioTools" /> may differ from the
/// default. Added in ABI 12.6.
/// </summary>
/// <param name="Timeout">
/// How long the run may take in total before the host ends it. Null is the
/// host's default of ten minutes; anything above sixty minutes is clamped to
/// sixty. A five-minute track through whisper large-v3 on a CPU takes about
/// twelve. With a <see cref="StallTimeout" /> this changes meaning: see there.
/// </param>
public sealed record PluginAudioRunOptions(TimeSpan? Timeout)
{
    /// <summary>The host's default: ten minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(10);

    /// <summary>The most a run may ask for: sixty minutes.</summary>
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromMinutes(60);

    /// <summary>
    /// For <see cref="IPluginAudioTools.RenderAsync" />: how long the run may
    /// go without making progress before the host ends it. Null, the default,
    /// judges the run by <see cref="Timeout" /> alone.
    /// <para>
    /// Some filters run slower than real time on a CPU - a RoFormer stem
    /// split, whisper - so no fixed total holds every track. With a stall
    /// limit the run is judged by its progress instead: it may take longer
    /// than sixty minutes for as long as it keeps moving. <see cref="Timeout" />
    /// then stops being clamped, and null for it means no total limit at all.
    /// </para>
    /// <para>
    /// Progress is ffmpeg's own report moving on: a later position in the
    /// output, or more bytes written. ffmpeg reports every five seconds
    /// whether or not anything moved, so a report that repeats the last
    /// position is not progress. The clock starts with the run, so the limit
    /// has to cover loading a model and the longest stretch a filter works
    /// before it hands anything on. Anything under ten seconds is raised to
    /// ten, twice the time between two reports, and anything above sixty
    /// minutes is clamped to sixty.
    /// </para>
    /// <para>
    /// <see cref="IPluginAudioTools.RunFilterGraphAsync(PluginAudioInput, PluginFilterGraph, Action{string}?, Action{string}?, PluginAudioRunOptions, CancellationToken)" />
    /// ignores it: that run reports no progress to judge it by. Added in ABI
    /// 12.7.
    /// </para>
    /// </summary>
    public TimeSpan? StallTimeout { get; init; }
}
