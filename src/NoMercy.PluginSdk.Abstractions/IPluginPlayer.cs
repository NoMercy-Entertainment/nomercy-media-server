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
/// What a plugin may ask a player to do.
///
/// A plugin never holds a player. The server has no player: the player lives in
/// whichever client the viewer is looking at, and there may be several. So a
/// plugin sends an intent to a session and the client that owns the player acts
/// on it.
///
/// That indirection is what makes casting work without a plugin knowing casting
/// exists. A client already decides whether audio comes out of its own speakers
/// or goes to a cast device, and an intent arriving at that client goes wherever
/// its audio was already going. A plugin handed a real player would have bound
/// itself to one output and broken the moment the viewer cast.
///
/// Typed convenience over <see cref="PluginCapability.Player" /> rather than a
/// mechanism of its own. Nothing here does anything an InvokeAsync call cannot,
/// which is deliberate: a capability reachable only through its own interface
/// would be one that older plugins could never discover.
/// </summary>
public interface IPluginPlayer
{
    /// <summary>
    /// Play a source. [source] is a URL the plugin is responsible for, which is
    /// how a radio plugin plays a stream the library has never heard of.
    /// </summary>
    Task PlayAsync(PluginPlaybackSource source, CancellationToken ct = default);

    /// <summary>Add to what is already playing rather than replacing it.</summary>
    Task EnqueueAsync(PluginPlaybackSource source, CancellationToken ct = default);

    /// <summary>Pause, resume, skip: the controls a viewer already has.</summary>
    Task ControlAsync(string command, CancellationToken ct = default);

    /// <summary>
    /// What is playing right now, as far as the server knows. Null when nothing
    /// is, or when no session has reported in.
    /// </summary>
    Task<PluginPlaybackState?> GetStateAsync(CancellationToken ct = default);

    /// <summary>
    /// What is lined up after the current item. A default member rather than a
    /// required one, so a host that cannot reach a queue says so at the call
    /// instead of failing every plugin that only ever plays.
    /// </summary>
    IPluginPlayerQueue Queue =>
        throw new PluginRefusedException(
            PluginRefusalMessages.FacadeNotOnThisHost(string.Empty, "IPluginContext.Player.Queue")
        );
}

/// <summary>
/// Something to play that the library does not own.
/// </summary>
public class PluginPlaybackSource
{
    /// <summary>Where the audio or video actually comes from.</summary>
    public required string Url { get; init; }

    /// <summary>What the viewer sees while it plays.</summary>
    public required string Title { get; init; }

    public string? Artist { get; init; }

    public string? ArtworkUrl { get; init; }

    /// <summary>
    /// A live stream has no end and no position to seek to. Saying so is what
    /// stops a client drawing a progress bar that never fills.
    /// </summary>
    public bool IsLive { get; init; }

    /// <summary>
    /// The plugin that owns this source, so a client can attribute what it is
    /// playing and a viewer can tell where it came from.
    /// </summary>
    public Ulid PluginId { get; init; }
}

/// <summary>
/// The commands a plugin may send, matching what a viewer can already do.
///
/// Named here so a plugin reaching the player through the generic bus and one
/// using the typed surface send the same words.
/// </summary>
public static class PluginPlaybackCommand
{
    public const string Play = "play";
    public const string Pause = "pause";
    public const string Stop = "stop";
    public const string Next = "next";
    public const string Previous = "previous";

    public static readonly string[] All = [Play, Pause, Stop, Next, Previous];

    public static bool IsKnown(string? command)
    {
        return command is not null && Array.IndexOf(All, command) >= 0;
    }
}

/// <summary>
/// What a session reported it was doing.
/// </summary>
public class PluginPlaybackState
{
    public required bool IsPlaying { get; init; }

    public string? Title { get; init; }

    /// <summary>
    /// Where the audio is actually going, which a plugin reads rather than
    /// decides. A plugin that tried to choose would be overruling the viewer.
    /// </summary>
    public string? Device { get; init; }

    public Ulid? OwnedByPlugin { get; init; }
}
