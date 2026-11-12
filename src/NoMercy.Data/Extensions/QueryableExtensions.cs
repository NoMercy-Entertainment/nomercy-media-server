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

using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;

namespace NoMercy.Data.Extensions;

public static class QueryableExtensions
{
    /// <summary>
    /// Filters entities to only those belonging to libraries the user has access to.
    /// Works with any entity that has a Library navigation property (Movie, Tv, Collection, Album).
    /// </summary>
    public static IQueryable<T> ForUser<T>(this IQueryable<T> query, Guid userId)
        where T : class, IHasLibrary
    {
        return query.Where(entity => entity.Library.LibraryUsers.Any(u => u.UserId == userId));
    }

    /// <summary>
    /// Filters libraries to only those the user has access to.
    /// </summary>
    public static IQueryable<Library> ForUser(this IQueryable<Library> query, Guid userId)
    {
        return query.Where(library => library.LibraryUsers.Any(u => u.UserId == userId));
    }

    /// <summary>
    /// Filters artists to only those belonging to libraries the user has access to.
    /// Artist has a nullable LibraryId, so it needs a separate overload.
    /// </summary>
    public static IQueryable<Artist> ForUser(this IQueryable<Artist> query, Guid userId)
    {
        return query.Where(artist => artist.Library.LibraryUsers.Any(u => u.UserId == userId));
    }

    /// <summary>
    /// Filters tracks to only those linked to a library the user has access to.
    /// </summary>
    public static IQueryable<Track> ForUser(this IQueryable<Track> query, Guid userId)
    {
        return query.Where(track =>
            track.LibraryTrack.Any(link => link.Library.LibraryUsers.Any(u => u.UserId == userId))
        );
    }
}
