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
/// Audio a plugin supplies for the join between two tracks of a listener: a
/// rendered transition, a station ident, a spoken introduction.
/// <para>
/// The listener's players play the first track up to
/// <paramref name="OutgoingStopMs" />, then the file, then the second track
/// from <paramref name="IncomingResumeMs" />. A file that begins with the
/// first track alone and ends with the second track alone lets a player
/// switch without a click.
/// </para>
/// <para>
/// The second track may be the first one: the file then stands in for a
/// stretch inside the track that plays. Added in ABI 12.14.
/// </para>
/// </summary>
/// <param name="FromTrackId">The track the file leads out of. It must be the track that plays.</param>
/// <param name="ToTrackId">
/// The track the file leads into: the listener's next track, or
/// <paramref name="FromTrackId" /> itself.
/// </param>
/// <param name="File">
/// The audio file, in the plugin's own folder, by its path relative to that
/// folder with forward slashes. The server serves it to the listener and to
/// nobody else.
/// </param>
/// <param name="OutgoingStopMs">Where the first track hands over to the file.</param>
/// <param name="IncomingResumeMs">Where the second track goes on after the file.</param>
/// <param name="DurationMs">The length of the file, 1 to <see cref="MaxDurationMs" />.</param>
public sealed record PluginMusicTransition(
    Guid FromTrackId,
    Guid ToTrackId,
    string File,
    int OutgoingStopMs,
    int IncomingResumeMs,
    int DurationMs
)
{
    /// <summary>The longest join a player may hold the server's advance for: ten minutes.</summary>
    public const int MaxDurationMs = 600_000;

    /// <summary>The most characters <see cref="Data" /> may hold.</summary>
    public const int MaxDataLength = 65_536;

    /// <summary>
    /// A JSON object the server passes on to the players unread, for what
    /// they show during the join. Null for none.
    /// </summary>
    public string? Data { get; init; }
}
