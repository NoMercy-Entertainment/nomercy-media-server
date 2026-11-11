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

using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Data.Plugins;

/// <summary>
/// How long one render may run, worked out from what the plugin asked for.
/// </summary>
/// <param name="Total">
/// The whole run, start to exit. <see cref="Timeout.InfiniteTimeSpan" /> when
/// the run is judged by its progress alone.
/// </param>
/// <param name="Stall">How long the run may go without progress. Null when it is judged by <paramref name="Total" /> alone.</param>
internal sealed record PluginAudioRunLimits(TimeSpan Total, TimeSpan? Stall)
{
    /// <summary>
    /// The longest a timer can be set for. A request above it cannot be
    /// kept, and is as good as no limit: it is seven weeks.
    /// </summary>
    private static readonly TimeSpan LongestTimer = TimeSpan.FromMilliseconds(uint.MaxValue - 1);

    /// <summary>
    /// The shortest stall limit a run is held to: twice the five seconds
    /// between two of ffmpeg's progress reports. A limit under one report
    /// period would end every run, however well it was moving, before the
    /// report that says so could arrive.
    /// </summary>
    internal static readonly TimeSpan ShortestStall = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Without a stall limit, the total the plugin asked for inside the
    /// maximum, as for a filter-graph run. With one, the run is judged by its
    /// progress: the total is what the plugin asked for, unclamped, or none
    /// at all. The stall limit itself is kept between
    /// <paramref name="shortestStall" /> and the maximum - an hour without a
    /// single sample is a stalled run by any measure.
    /// </summary>
    internal static PluginAudioRunLimits ForRender(
        PluginAudioRunOptions? options,
        TimeSpan fallback,
        TimeSpan shortestStall
    )
    {
        if (options?.StallTimeout is not { } stall || stall <= TimeSpan.Zero)
            return new(PluginAudioTools.EffectiveTimeout(options, fallback), null);

        TimeSpan total =
            options.Timeout is { } requested
            && requested > TimeSpan.Zero
            && requested <= LongestTimer
                ? requested
                : Timeout.InfiniteTimeSpan;

        if (stall < shortestStall)
            stall = shortestStall;

        if (stall > PluginAudioRunOptions.MaxTimeout)
            stall = PluginAudioRunOptions.MaxTimeout;

        return new(total, stall);
    }
}
