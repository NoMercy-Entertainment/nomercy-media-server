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
using NoMercy.Database.Models.Libraries;

namespace NoMercy.Database.Models.TvShows;

/// <summary>
/// The one rule for which shows a library lists: a show with a file on disk, or a
/// show the owner added by hand, with or without a file yet. A show a scan attached
/// on a guess, with nothing on disk, stays out.
/// <para>
/// Every library surface (home carousel, library cards, paginated list and cards)
/// filters through <see cref="IsListed"/>, so they cannot drift apart again: the
/// manual-add rule once lived in the carousel alone, and a show the owner had just
/// added showed on the home screen but not on the library page.
/// </para>
/// </summary>
public static class TvLibraryVisibility
{
    // A multi-episode file lives on its own (first) episode, so "an episode has a
    // file" already covers the episodes such a file spans.
    public static readonly Expression<Func<Tv, bool>> IsListed = tv =>
        tv.Episodes.Any(episode => episode.VideoFiles.Any(file => file.Folder != null))
        || tv.Library.LibraryTvs.Any(link =>
            link.TvId == tv.Id && link.AddedBy == LibraryLinkOrigin.Manual
        );

    public static IQueryable<Tv> ListedInLibrary(this IQueryable<Tv> shows) =>
        shows.Where(IsListed);
}
