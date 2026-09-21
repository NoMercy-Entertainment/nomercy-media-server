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
/// What a live player can be asked to do, by any client, for any plugin channel.
///
/// A tuner plugin supplies channels; it does not supply a remote control. These
/// are the buttons the host already draws, so a channel from a plugin behaves
/// like a channel from anywhere else on every client at once.
/// </summary>
public static class PluginLiveAction
{
    public const string ChannelUp = "channel-up";
    public const string ChannelDown = "channel-down";
    public const string ChannelNumber = "channel-number";
    public const string LastChannel = "last-channel";
    public const string MiniGuide = "mini-guide";
    public const string JumpToLive = "jump-to-live";
    public const string StartOver = "start-over";
    public const string Record = "record";
    public const string Quality = "quality";

    public static IReadOnlyList<string> All { get; } =
    [
        ChannelUp,
        ChannelDown,
        ChannelNumber,
        LastChannel,
        MiniGuide,
        JumpToLive,
        StartOver,
        Record,
        Quality,
    ];

    public static bool IsKnown(string? action)
    {
        return action is not null && All.Contains(action);
    }
}
