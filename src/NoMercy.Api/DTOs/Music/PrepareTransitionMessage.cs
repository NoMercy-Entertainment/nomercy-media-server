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

using Newtonsoft.Json;

namespace NoMercy.Api.DTOs.Music;

/// <summary>
/// <c>PrepareTransition</c>, music hub to a user's clients: audio to play
/// between the playing track and <see cref="NextItem" />, sent ten seconds
/// before the playing track reaches <c>outgoing_stop_ms</c>.
/// <para>
/// The player that sounds plays the track up to that stop, then the file,
/// then <see cref="NextItem" /> from <c>incoming_resume_ms</c>. It says so
/// with <c>CrossfadeStartCommand(duration_ms)</c> when it switches to the file
/// and <c>CrossfadeCompleteAtCommand(next_item.id, incoming_resume_ms)</c> at
/// its end. A file that is not loaded in time is left out, and the crossfade
/// on <c>PrepareCrossfade</c> happens as it always did.
/// </para>
/// <para>
/// A new message and not a new field on <c>PrepareCrossfade</c>, which is
/// still sent exactly when and as it was: a client that does not know this
/// message never sees a difference.
/// </para>
/// </summary>
public sealed class PrepareTransitionMessage
{
    /// <summary>
    /// The track the file leads into. It can be the track that already plays:
    /// the file then stands in for a stretch inside that track.
    /// </summary>
    [JsonProperty("next_item")]
    public required PlaylistTrackDto NextItem { get; init; }

    [JsonProperty("transition")]
    public required MusicTransitionDto Transition { get; init; }

    [JsonProperty("timestamp")]
    public required long Timestamp { get; init; }
}
