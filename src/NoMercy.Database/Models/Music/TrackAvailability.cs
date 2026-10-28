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

using System.Linq.Expressions;

namespace NoMercy.Database.Models.Music;

/// <summary>
/// The one rule for which tracks a user is shown: a track whose file went missing
/// (<see cref="Track.MissingSince" />) stays in the database but leaves every list.
/// <see cref="Playable(IQueryable{Track})" /> and its link-table overloads apply it to a
/// query; <see cref="TrackIsPlayable" /> and the link predicates apply it inside an include
/// or a projection.
/// </summary>
public static class TrackAvailability
{
    public static readonly Expression<Func<Track, bool>> TrackIsPlayable = track =>
        track.MissingSince == null;

    public static readonly Expression<Func<AlbumTrack, bool>> AlbumTrackIsPlayable = link =>
        link.Track.MissingSince == null;

    public static readonly Expression<Func<ArtistTrack, bool>> ArtistTrackIsPlayable = link =>
        link.Track.MissingSince == null;

    public static readonly Expression<Func<PlaylistTrack, bool>> PlaylistTrackIsPlayable = link =>
        link.Track.MissingSince == null;

    public static readonly Expression<Func<MusicGenreTrack, bool>> GenreTrackIsPlayable = link =>
        link.Track.MissingSince == null;

    public static readonly Expression<Func<TrackUser, bool>> TrackUserIsPlayable = link =>
        link.Track.MissingSince == null;

    public static readonly Expression<Func<LibraryTrack, bool>> LibraryTrackIsPlayable = link =>
        link.Track.MissingSince == null;

    public static IQueryable<Track> Playable(this IQueryable<Track> tracks) =>
        tracks.Where(TrackIsPlayable);

    public static IQueryable<AlbumTrack> Playable(this IQueryable<AlbumTrack> links) =>
        links.Where(AlbumTrackIsPlayable);

    public static IQueryable<ArtistTrack> Playable(this IQueryable<ArtistTrack> links) =>
        links.Where(ArtistTrackIsPlayable);

    public static IQueryable<PlaylistTrack> Playable(this IQueryable<PlaylistTrack> links) =>
        links.Where(PlaylistTrackIsPlayable);

    public static IQueryable<MusicGenreTrack> Playable(this IQueryable<MusicGenreTrack> links) =>
        links.Where(GenreTrackIsPlayable);

    public static IQueryable<TrackUser> Playable(this IQueryable<TrackUser> links) =>
        links.Where(TrackUserIsPlayable);

    public static IQueryable<LibraryTrack> Playable(this IQueryable<LibraryTrack> links) =>
        links.Where(LibraryTrackIsPlayable);
}
