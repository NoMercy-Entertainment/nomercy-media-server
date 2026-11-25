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
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;

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
        foreach (ImageColumn column in Columns)
            stored.AddRange(await column.Query(context).Distinct().ToListAsync(cancellationToken));

        List<InboxItem> inbox = await context
            .InboxItems.AsNoTracking()
            .ToListAsync(cancellationToken);
        foreach (InboxItem item in inbox)
        {
            stored.AddRange(item.Candidates.Select(candidate => candidate.PosterPath));
            stored.Add(item.SelectedMatch?.PosterPath);
        }

        return stored
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => path!.Replace("/", "").Replace("\\", ""))
            .ToHashSet();
    }

    /// <summary>Every column that holds the path of an image the cache may store in original/.</summary>
    public static IReadOnlyList<ImageColumn> Columns { get; } =
    [
        Of(c => c.Images, x => x.FilePath),
        Of(c => c.Movies, x => x.Poster),
        Of(c => c.Movies, x => x.Backdrop),
        Of(c => c.Tvs, x => x.Poster),
        Of(c => c.Tvs, x => x.Backdrop),
        Of(c => c.Seasons, x => x.Poster),
        Of(c => c.Episodes, x => x.Still),
        Of(c => c.People, x => x.Profile),
        Of(c => c.Collections, x => x.Poster),
        Of(c => c.Collections, x => x.Backdrop),
        Of(c => c.Networks, x => x.Logo),
        Of(c => c.Companies, x => x.Logo),
        Of(c => c.WatchProviders, x => x.Logo),
        Of(c => c.Recommendations, x => x.Poster),
        Of(c => c.Recommendations, x => x.Backdrop),
        Of(c => c.Similar, x => x.Poster),
        Of(c => c.Similar, x => x.Backdrop),
        Of(c => c.Specials, x => x.Poster),
        Of(c => c.Specials, x => x.Backdrop),
        Of(c => c.Specials, x => x.Logo),
        Of(c => c.Libraries, x => x.Image),
        Of(c => c.UserPlaylists, x => x.Cover),
    ];

    private static ImageColumn Of<TEntity>(
        Func<MediaContext, DbSet<TEntity>> set,
        Expression<Func<TEntity, string?>> column
    )
        where TEntity : class =>
        new(
            typeof(TEntity),
            ((MemberExpression)column.Body).Member.Name,
            context => set(context).Select(column)
        );
}
