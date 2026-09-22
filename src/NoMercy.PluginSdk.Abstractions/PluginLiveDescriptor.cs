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
/// One live channel, complete enough for any client to play and drive it.
///
/// <para>
/// The host's answer, built from the <see cref="PluginLiveChannel" /> a plugin
/// supplies. Links are prioritised and fall through, and the host resolves them
/// server side, which is why a credential-bearing upstream url never reaches a
/// device: what a client receives names the channel, what it offers and its
/// guide, and plays through the host's own proxy url.
/// </para>
///
/// <para>
/// What a channel offers is declared rather than assumed. A player that drew
/// Start over and Record on every channel offered two buttons that did nothing
/// on most of them.
/// </para>
/// </summary>
public sealed record PluginLiveDescriptor(
    string ChannelId,
    int Number,
    string Name,
    string? Group,
    Uri? Logo,
    bool Adult,
    string MediaKind,
    IReadOnlyList<PluginLiveLink> Links,
    bool CatchUp,
    bool StartOver,
    bool Record,
    IReadOnlyList<string> Qualities,
    IReadOnlyList<PluginEpgProgramme> Guide
)
{
    public const string Hls = "hls";
    public const string ContinuousTs = "ts";
    public const string Radio = "radio";

    /// <summary>The kinds a client knows how to play. Anything else is a channel no client can open.</summary>
    public static IReadOnlyList<string> MediaKinds { get; } = [Hls, ContinuousTs, Radio];

    /// <summary>The upstreams in the order they are tried, lowest priority first.</summary>
    public IReadOnlyList<PluginLiveLink> LinksInOrder =>
        Links.OrderBy(link => link.Priority).ToList();

    /// <summary>What this channel offers, out of <see cref="PluginLiveAction" />.</summary>
    public IReadOnlyList<string> SupportedActions()
    {
        List<string> actions =
        [
            PluginLiveAction.ChannelUp,
            PluginLiveAction.ChannelDown,
            PluginLiveAction.ChannelNumber,
            PluginLiveAction.LastChannel,
            PluginLiveAction.MiniGuide,
            PluginLiveAction.JumpToLive,
        ];

        if (StartOver)
        {
            actions.Add(PluginLiveAction.StartOver);
        }

        if (Record)
        {
            actions.Add(PluginLiveAction.Record);
        }

        if (Qualities.Count > 0)
        {
            actions.Add(PluginLiveAction.Quality);
        }

        return actions;
    }
}
