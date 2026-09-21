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

/// <summary>The stretch of time a guide is drawn against.</summary>
public sealed record PluginGuideWindow(DateTimeOffset From, DateTimeOffset To)
{
    public int Minutes => (int)(To - From).TotalMinutes;
}

/// <summary>One programme, measured against the window it is drawn in.</summary>
public sealed record PluginGuideItem(
    string ProgrammeId,
    string Title,
    int OffsetMinutes,
    int WidthMinutes,
    bool ClippedStart,
    bool ClippedStop
);

/// <summary>One channel's evening, whether or not anything was scheduled on it.</summary>
public sealed record PluginGuideRow(
    string ChannelId,
    int Number,
    string Name,
    IReadOnlyList<PluginGuideItem> Items
);

/// <summary>
/// The guide, laid out once on the server.
///
/// <para>
/// Here rather than on each client, because every client would otherwise carry
/// its own copy of the same arithmetic and drift from the others. What a client
/// receives is the ordinary component vocabulary, already laid out.
/// </para>
/// </summary>
public static class PluginGuideLayout
{
    /// <summary>
    /// One row per channel, always, and every programme measured against the
    /// window.
    ///
    /// <para>
    /// A guide that only emits rows for channels with data leaves holes where a
    /// provider sent nothing, and the row under the hole then reads as the wrong
    /// channel's schedule.
    /// </para>
    /// </summary>
    public static IReadOnlyList<PluginGuideRow> Layout(
        IReadOnlyList<PluginLiveDescriptor> channels,
        IReadOnlyList<PluginEpgProgramme> programmes,
        PluginGuideWindow window
    )
    {
        int span = window.Minutes;

        return
        [
            .. channels
                .OrderBy(channel => channel.Number)
                .Select(channel => new PluginGuideRow(
                    channel.ChannelId,
                    channel.Number,
                    channel.Name,
                    [
                        .. programmes
                            .Where(programme => programme.ChannelId == channel.ChannelId)
                            .Where(programme =>
                                programme.Stop > window.From && programme.Start < window.To
                            )
                            .OrderBy(programme => programme.Start)
                            .Select(programme =>
                            {
                                int offset = Math.Max(
                                    0,
                                    MinutesBetween(window.From, programme.Start)
                                );
                                int end = Math.Min(
                                    span,
                                    MinutesBetween(window.From, programme.Stop)
                                );

                                return new PluginGuideItem(
                                    programme.ProgrammeId,
                                    programme.Title,
                                    offset,
                                    end - offset,
                                    programme.Start < window.From,
                                    programme.Stop > window.To
                                );
                            }),
                    ]
                )),
        ];
    }

    /// <summary>Where the clock falls inside the window, or null when the window is elsewhere.</summary>
    public static int? NowOffset(PluginGuideWindow window, DateTimeOffset now)
    {
        if (now < window.From || now > window.To)
        {
            return null;
        }

        return MinutesBetween(window.From, now);
    }

    /// <summary>The channels in the order the viewer knows them in, which is by number.</summary>
    public static IReadOnlyList<PluginLiveDescriptor> InOrder(
        IReadOnlyList<PluginLiveDescriptor> channels
    )
    {
        return [.. channels.OrderBy(channel => channel.Number)];
    }

    private static int MinutesBetween(DateTimeOffset from, DateTimeOffset to)
    {
        return (int)Math.Round((to - from).TotalMinutes);
    }
}
