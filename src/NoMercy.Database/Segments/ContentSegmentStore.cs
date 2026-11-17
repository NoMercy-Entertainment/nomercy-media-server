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
using Microsoft.EntityFrameworkCore.Storage;
using NoMercy.Database.Models.Media;

namespace NoMercy.Database.Segments;

/// <summary>
/// The one way automatic writers store a <see cref="ContentSegmentWritePlan"/>.
/// A plan is made from a snapshot, and a person can add or edit a segment in
/// the dashboard before it is saved. So, inside the transaction of the save,
/// the manual rows of the affected items are read again: a planned insert of a
/// kind that now has a manual row is dropped, and a planned delete of a row
/// that has become manual is skipped.
/// </summary>
public static class ContentSegmentStore
{
    /// <returns>The number of rows inserted.</returns>
    public static async Task<int> ApplySegmentPlansAsync(
        this MediaContext context,
        IReadOnlyList<ContentSegmentWritePlan> plans,
        CancellationToken ct
    )
    {
        List<ContentSegmentWritePlan> work = [.. plans.Where(plan => !plan.IsEmpty)];
        if (work.Count == 0)
            return 0;

        // A transaction the caller already opened stays the caller's to commit.
        // A provider without transactions (the in-memory one tests use) has no
        // window to close.
        await using IDbContextTransaction? transaction =
            context.Database.CurrentTransaction is null && context.Database.IsRelational()
                ? await context.Database.BeginTransactionAsync(ct)
                : null;

        List<ContentSegment> manual = await ManualRowsAsync(context, work, ct);
        HashSet<Ulid> manualIds = [.. manual.Select(row => row.Id)];
        HashSet<(int?, int?, ContentSegmentType)> manualKinds =
        [
            .. manual.Select(row =>
                (row.EpisodeId, row.MovieId, ContentSegmentPrecedence.KindOf(row.SegmentType))
            ),
        ];

        DateTime now = DateTime.UtcNow;
        int inserted = 0;
        foreach (ContentSegmentWritePlan plan in work)
        {
            foreach (ContentSegment stale in plan.Remove.Where(row => !manualIds.Contains(row.Id)))
            {
                // The plan's rows were read untracked; the context may already
                // track the same row, and EF refuses a second instance of a key.
                ContentSegment? tracked = context.ContentSegments.Local.FirstOrDefault(row =>
                    row.Id == stale.Id
                );
                context.ContentSegments.Remove(tracked ?? stale);
            }

            foreach (ContentSegment segment in plan.Add)
            {
                if (
                    manualKinds.Contains(
                        (
                            segment.EpisodeId,
                            segment.MovieId,
                            ContentSegmentPrecedence.KindOf(segment.SegmentType)
                        )
                    )
                )
                    continue;

                segment.CreatedAt = now;
                segment.UpdatedAt = now;
                context.ContentSegments.Add(segment);
                inserted++;
            }
        }

        await context.SaveChangesAsync(ct);
        if (transaction is not null)
            await transaction.CommitAsync(ct);

        return inserted;
    }

    private static Task<List<ContentSegment>> ManualRowsAsync(
        MediaContext context,
        List<ContentSegmentWritePlan> plans,
        CancellationToken ct
    )
    {
        List<ContentSegment> touched = [.. plans.SelectMany(plan => plan.Add.Concat(plan.Remove))];
        List<int> episodeIds =
        [
            .. touched
                .Where(row => row.EpisodeId is not null)
                .Select(row => row.EpisodeId!.Value)
                .Distinct(),
        ];
        List<int> movieIds =
        [
            .. touched
                .Where(row => row.MovieId is not null)
                .Select(row => row.MovieId!.Value)
                .Distinct(),
        ];

        return context
            .ContentSegments.AsNoTracking()
            .Where(row =>
                row.Source == ContentSegmentSource.Manual
                && (
                    (row.EpisodeId != null && episodeIds.Contains(row.EpisodeId.Value))
                    || (row.MovieId != null && movieIds.Contains(row.MovieId.Value))
                )
            )
            .ToListAsync(ct);
    }
}
