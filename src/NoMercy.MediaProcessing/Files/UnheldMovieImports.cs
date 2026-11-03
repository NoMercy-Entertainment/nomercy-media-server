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

using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Add content on a movie library can select a movie the server does not hold yet. Such a
/// file has no movie row to encode against, so the movie is imported first and its
/// selected file rides along on that import.
/// </summary>
public static class UnheldMovieImports
{
    /// <summary>
    /// Queues one <see cref="MovieImportJob"/> per unheld movie, carrying that movie's files.
    /// </summary>
    /// <returns>
    /// The files that still take the ordinary path: the ones whose movie the server holds,
    /// and the ones whose id is not a movie id.
    /// </returns>
    public static List<EncodeAfterImportFile> Dispatch(
        IJobDispatcher jobDispatcher,
        Library library,
        IReadOnlyList<EncodeAfterImportFile> files,
        IReadOnlySet<int> heldMovieIds
    )
    {
        List<EncodeAfterImportFile> remaining = [];
        Dictionary<int, List<EncodeAfterImportFile>> unheld = [];

        foreach (EncodeAfterImportFile file in files)
        {
            if (!int.TryParse(file.Id, out int movieId) || heldMovieIds.Contains(movieId))
            {
                remaining.Add(file);
                continue;
            }

            if (!unheld.TryGetValue(movieId, out List<EncodeAfterImportFile>? group))
                unheld[movieId] = group = [];
            group.Add(file);
        }

        foreach ((int movieId, List<EncodeAfterImportFile> group) in unheld)
        {
            jobDispatcher.DispatchJob(
                new MovieImportJob
                {
                    Id = movieId,
                    LibraryId = library.Id,
                    EncodeAfterImport = group,
                }
            );
        }

        return remaining;
    }
}
