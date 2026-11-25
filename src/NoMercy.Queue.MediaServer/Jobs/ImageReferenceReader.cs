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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;

namespace NoMercy.Queue.MediaServer.Jobs;

/// <summary>
/// The file names of every image an item refers to. A name is the stored path
/// without its slash, the way the image clients name the cached file.
/// </summary>
public class ImageReferenceReader(IDbContextFactory<MediaContext> contextFactory)
{
    public async Task<HashSet<string>> ReadAsync(CancellationToken cancellationToken)
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(
            cancellationToken
        );

        List<string?> stored = [];
        stored.AddRange(await Column(context.Images.Select(x => x.FilePath)));
        stored.AddRange(await Column(context.Movies.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Movies.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Tvs.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Tvs.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Seasons.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Episodes.Select(x => x.Still)));
        stored.AddRange(await Column(context.People.Select(x => x.Profile)));
        stored.AddRange(await Column(context.Collections.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Collections.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Networks.Select(x => x.Logo)));
        stored.AddRange(await Column(context.Companies.Select(x => x.Logo)));
        stored.AddRange(await Column(context.WatchProviders.Select(x => x.Logo)));
        stored.AddRange(await Column(context.Recommendations.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Recommendations.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Similar.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Similar.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Specials.Select(x => x.Poster)));
        stored.AddRange(await Column(context.Specials.Select(x => x.Backdrop)));
        stored.AddRange(await Column(context.Specials.Select(x => x.Logo)));

        return stored
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace("/", "").Replace("\\", ""))
            .ToHashSet();

        async Task<List<string?>> Column(IQueryable<string?> query) =>
            await query.Distinct().ToListAsync(cancellationToken);
    }
}
