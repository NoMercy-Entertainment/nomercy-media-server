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
using Microsoft.Extensions.Logging;
using NoMercy.Database;
using NoMercy.MediaProcessing.Common;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.MediaProcessing.Shows;

public class AnimeClassificationAuditService(
    IDbContextFactory<MediaContext> contextFactory,
    IMediaTypeClassifier mediaTypeClassifier,
    ILogger<AnimeClassificationAuditService> logger
) : IAnimeClassificationAuditService
{
    public async Task<IReadOnlyList<AnimeClassificationMismatch>> FindMismatchesAsync(
        CancellationToken ct = default
    )
    {
        await using MediaContext context = await contextFactory.CreateDbContextAsync(ct);

        List<TvLibraryProjection> rows = await context
            .Tvs.AsNoTracking()
            .Select(tv => new TvLibraryProjection(
                tv.Id,
                tv.Title,
                tv.FirstAirDate,
                tv.Library.Type,
                tv.OriginCountry
            ))
            .ToListAsync(ct);

        List<AnimeClassificationMismatch> mismatches = [];

        foreach (TvLibraryProjection row in rows)
        {
            string? classifiedType = await mediaTypeClassifier.ClassifyAsync(
                row.Title,
                row.FirstAirDate.ParseYear(),
                OriginCountries.Parse(row.OriginCountry)
            );

            // An inconclusive lookup (provider failure, e.g. AniList rate-limiting a
            // run over hundreds of shows) is not a verdict — reporting it as a
            // mismatch would flood the audit with false positives on every show the
            // lookup happened to fail for.
            if (classifiedType is null || classifiedType == row.LibraryType)
                continue;

            mismatches.Add(new(row.Id, row.Title, row.LibraryType, classifiedType));

            logger.LogWarning(
                "Show {Id} \"{Title}\": classified as {ClassifiedType} but filed under a {LibraryType} library",
                [row.Id, row.Title, classifiedType, row.LibraryType]
            );
        }

        return mismatches;
    }

    private sealed record TvLibraryProjection(
        int Id,
        string Title,
        DateTime? FirstAirDate,
        string LibraryType,
        string? OriginCountry
    );
}
