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
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;

namespace NoMercy.MediaProcessing.Files;

/// <summary>
/// Add content on a TV library can select episodes of a show the server does not hold
/// yet. Such a file has no episode row to encode against, so the show is imported once
/// and its selected files ride along on that import.
/// </summary>
public static class UnheldShowImports
{
    /// <summary>
    /// Queues one <see cref="ShowImportJob"/> per unheld show, carrying that show's files.
    /// </summary>
    /// <param name="heldEpisodeIds">The selected ids that already name an episode row.</param>
    /// <returns>
    /// The files that still take the ordinary path: the ones whose episode the server
    /// holds, and the ones whose show could not be found from the name.
    /// </returns>
    public static async Task<List<EncodeAfterImportFile>> DispatchAsync(
        IJobDispatcher jobDispatcher,
        IFilenameParserPipeline pipeline,
        Library library,
        IReadOnlyList<EncodeAfterImportFile> files,
        IReadOnlySet<int> heldEpisodeIds
    )
    {
        List<EncodeAfterImportFile> remaining = [];
        Dictionary<int, List<EncodeAfterImportFile>> unheld = [];

        foreach (EncodeAfterImportFile file in files)
        {
            if (int.TryParse(file.Id, out int episodeId) && heldEpisodeIds.Contains(episodeId))
            {
                remaining.Add(file);
                continue;
            }

            (int ShowId, int Season, int Episode)? located = await UnheldShowLocator.LocateAsync(
                pipeline,
                file.InputFile,
                library.Type
            );
            if (located is null)
            {
                remaining.Add(file);
                continue;
            }

            if (!unheld.TryGetValue(located.Value.ShowId, out List<EncodeAfterImportFile>? group))
                unheld[located.Value.ShowId] = group = [];
            group.Add(file);
        }

        foreach ((int showId, List<EncodeAfterImportFile> group) in unheld)
        {
            jobDispatcher.DispatchJob(
                new ShowImportJob
                {
                    Id = showId,
                    LibraryId = library.Id,
                    AddedBy = LibraryLinkOrigin.Manual,
                    EncodeAfterImport = group,
                }
            );
        }

        return remaining;
    }
}
