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
/// A live channel drawn out of the ordinary component vocabulary.
///
/// <para>
/// No component here belongs to live television. A control strip is a row of
/// buttons, a guide is a table, and a channel is a card: every client already
/// draws all three, so a tuner plugin reaches every screen without a single
/// client learning a word about channels.
/// </para>
/// </summary>
public static class PluginLiveViews
{
    /// <summary>
    /// The buttons this channel offers, and only those.
    ///
    /// <para>
    /// The action each button carries comes from the plugin, because the plugin
    /// is what knows how to serve it. A player that drew Start over and Record
    /// on every channel offered two buttons that did nothing on most of them.
    /// </para>
    /// </summary>
    public static PluginComponent Controls(
        string id,
        PluginLiveDescriptor channel,
        Func<string, PluginActionIntent> intentFor,
        Func<string, string>? labelFor = null
    )
    {
        return PluginViews.Row(
            id,
            [
                .. channel
                    .SupportedActions()
                    .Select(action =>
                        PluginViews.Button(
                            $"{id}-{action}",
                            labelFor?.Invoke(action) ?? action,
                            intentFor(action)
                        )
                    ),
            ]
        );
    }

    /// <summary>
    /// The channel strip, in the order the viewer knows the channels in.
    ///
    /// <para>
    /// A strip in provider order put channel 101 between 3 and 4, and the
    /// remote's channel keys then walked a different sequence from the strip on
    /// the screen.
    /// </para>
    /// </summary>
    public static PluginComponent ChannelStrip(
        string id,
        IReadOnlyList<PluginLiveDescriptor> channels,
        string? current,
        Func<PluginLiveDescriptor, PluginActionIntent> intentFor
    )
    {
        return PluginViews.Row(
            id,
            [
                .. PluginGuideLayout
                    .InOrder(channels)
                    .Select(channel =>
                        PluginViews.Button(
                            $"{id}-{channel.ChannelId}",
                            $"{channel.Number} {channel.Name}",
                            intentFor(channel),
                            variant: channel.ChannelId == current ? "primary" : "secondary"
                        )
                    ),
            ]
        );
    }

    /// <summary>
    /// The guide, as a table of half hours.
    ///
    /// <para>
    /// A table rather than bars on a timeline: a bar is a position a generic
    /// component has no word for, and every client would have to be taught one.
    /// A row of half-hour columns says the same thing with the vocabulary every
    /// screen already has, and a reader announces it as a table.
    /// </para>
    /// </summary>
    public static PluginComponent Guide(
        string id,
        IReadOnlyList<PluginLiveDescriptor> channels,
        IReadOnlyList<PluginEpgProgramme> programmes,
        PluginGuideWindow window,
        string channelHeading,
        string nothingScheduled,
        Func<DateTimeOffset, string> clock,
        Func<PluginGuideRow, PluginGuideItem, PluginActionIntent>? intentFor = null
    )
    {
        IReadOnlyList<PluginGuideRow> rows = PluginGuideLayout.Layout(channels, programmes, window);
        int[] slots = Slots(window);

        List<PluginTableColumn> columns =
        [
            new() { Key = "channel", Label = channelHeading },
            .. slots.Select(minute => new PluginTableColumn
            {
                Key = SlotKey(minute),
                Label = clock(window.From.AddMinutes(minute)),
            }),
        ];

        return PluginViews.Table(
            id,
            columns,
            [
                .. rows.Select(row =>
                    PluginViews.Row(
                        $"{id}-{row.ChannelId}",
                        Cells(row, slots, nothingScheduled),
                        intentFor is null || row.Items.Count == 0
                            ? null
                            : intentFor(row, row.Items[0])
                    )
                ),
            ]
        );
    }

    /// <summary>
    /// What is on this channel in each half hour.
    ///
    /// <para>
    /// A programme covering several slots names itself in each one it covers: a
    /// film written only into the slot it started in left the rest of the row
    /// empty, which reads as a channel that stops broadcasting at half past.
    /// </para>
    /// </summary>
    private static Dictionary<string, object?> Cells(
        PluginGuideRow row,
        int[] slots,
        string nothingScheduled
    )
    {
        Dictionary<string, object?> cells = new() { ["channel"] = $"{row.Number} {row.Name}" };

        foreach (int minute in slots)
        {
            PluginGuideItem? item = row.Items.FirstOrDefault(one =>
                one.OffsetMinutes <= minute && one.OffsetMinutes + one.WidthMinutes > minute
            );

            cells[SlotKey(minute)] =
                item?.Title ?? (row.Items.Count == 0 ? nothingScheduled : null);
        }

        return cells;
    }

    private static int[] Slots(PluginGuideWindow window)
    {
        return [.. Enumerable.Range(0, Math.Max(1, window.Minutes / 30)).Select(slot => slot * 30)];
    }

    private static string SlotKey(int minute)
    {
        return $"t{minute}";
    }
}
