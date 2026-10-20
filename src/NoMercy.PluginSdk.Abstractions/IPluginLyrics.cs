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
/// Reading and writing the lyrics of a track: the write side of what
/// <c>GET music/tracks/{id}/lyrics</c> serves. A plugin that aligns known lyric
/// text to the vocal stem writes line and word timings here, under rules the
/// host enforces: synced provider lyrics are never overwritten, a write names
/// the version it read, and the text keeps its own attribution.
/// <para>
/// Elevated - see <see cref="PluginHookCapability.LyricsWrite" /> - because
/// what lands here is what every client shows under the player.
/// </para>
/// <para>Added in ABI 12.6.</para>
/// </summary>
public interface IPluginLyrics
{
    /// <summary>
    /// The lyrics a track has now, with their provenance and version. Null when
    /// the host does not know the track.
    /// </summary>
    Task<PluginLyricsState?> GetStateAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Every validated lyric text the host can find for a track: each
    /// provider candidate, not just the winner, and the lyrics embedded in
    /// the file's own tags. Provider calls run inside the host's rate limits
    /// and backoff, and the answer is kept for a day, so a plugin may ask
    /// freely. Empty when nothing was found or the track is unknown.
    /// </summary>
    Task<PluginLyricsText> GetTextCandidatesAsync(Guid trackId, CancellationToken ct = default);

    /// <summary>
    /// Replaces the lyrics of a track with generated timing. Refused, as a
    /// <see cref="PluginWriteResult" /> whose reason starts with one of the
    /// <see cref="PluginLyricsRefusal" /> codes, when the version moved, when
    /// the track has synced provider lyrics, when the lines are not a valid
    /// timeline, or when the source is not one the host knows. On success the
    /// timing is <c>generated</c>, the source is the one given, the offset is
    /// cleared and the version is bumped.
    /// </summary>
    Task<PluginWriteResult> WriteAsync(
        Guid trackId,
        PluginLyricsWrite write,
        CancellationToken ct = default
    );

    /// <summary>
    /// Removes generated lyrics so the server's own resolver runs again on the
    /// next request. Refused with <see cref="PluginLyricsRefusal.NotGenerated" />
    /// when the timing is anything else, and with
    /// <see cref="PluginLyricsRefusal.VersionConflict" /> when the version moved.
    /// </summary>
    Task<PluginWriteResult> ClearAsync(
        Guid trackId,
        long expectedVersion,
        CancellationToken ct = default
    );
}
