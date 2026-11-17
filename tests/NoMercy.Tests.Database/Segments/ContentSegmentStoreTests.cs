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

using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Segments;

namespace NoMercy.Tests.Database.Segments;

/// <summary>
/// A plan is made from a snapshot; a person may add or edit a segment before
/// it is saved. The shared apply step must never put an automatic row beside
/// that manual one, nor delete it.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ContentSegmentStoreTests : IDisposable
{
    private const int EpisodeId = 7;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public ContentSegmentStoreTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task A_plan_without_interference_is_stored()
    {
        ContentSegment detector = Insert(ContentSegmentSource.Detector, ContentSegmentType.Intro);
        ContentSegmentWritePlan plan = PlanChapterIntro([detector]);

        await Apply(plan);

        Rows().Should().ContainSingle().Which.Source.Should().Be(ContentSegmentSource.Chapters);
    }

    [Fact]
    public async Task A_manual_row_added_after_the_snapshot_blocks_the_planned_insert()
    {
        ContentSegmentWritePlan plan = PlanChapterIntro([]);
        Insert(ContentSegmentSource.Manual, ContentSegmentType.Intro);

        int inserted = await Apply(plan);

        inserted.Should().Be(0);
        Rows().Should().ContainSingle().Which.Source.Should().Be(ContentSegmentSource.Manual);
    }

    [Fact]
    public async Task Manual_credits_added_after_the_snapshot_block_a_planned_outro()
    {
        ContentSegmentWritePlan plan = ContentSegmentPrecedence.Plan(
            [],
            [Row(ContentSegmentSource.Chapters, ContentSegmentType.Outro)],
            ContentSegmentSource.Chapters
        );
        Insert(ContentSegmentSource.Manual, ContentSegmentType.Credits);

        await Apply(plan);

        Rows().Should().ContainSingle().Which.SegmentType.Should().Be(ContentSegmentType.Credits);
    }

    [Fact]
    public async Task A_row_edited_into_a_manual_one_after_the_snapshot_is_not_deleted()
    {
        ContentSegment detector = Insert(ContentSegmentSource.Detector, ContentSegmentType.Intro);
        ContentSegmentWritePlan plan = PlanChapterIntro([detector]);
        await using (MediaContext edit = new(_options))
        {
            ContentSegment row = await edit.ContentSegments.SingleAsync(r => r.Id == detector.Id);
            row.Source = ContentSegmentSource.Manual;
            await edit.SaveChangesAsync();
        }

        await Apply(plan);

        Rows()
            .Should()
            .ContainSingle()
            .Which.Should()
            .Match<ContentSegment>(r =>
                r.Id == detector.Id && r.Source == ContentSegmentSource.Manual
            );
    }

    [Fact]
    public async Task A_manual_row_of_another_item_changes_nothing()
    {
        ContentSegmentWritePlan plan = PlanChapterIntro([]);
        Insert(ContentSegmentSource.Manual, ContentSegmentType.Intro, episodeId: EpisodeId + 1);

        int inserted = await Apply(plan);

        inserted.Should().Be(1);
    }

    private static ContentSegmentWritePlan PlanChapterIntro(List<ContentSegment> snapshot) =>
        ContentSegmentPrecedence.Plan(
            snapshot,
            [Row(ContentSegmentSource.Chapters, ContentSegmentType.Intro)],
            ContentSegmentSource.Chapters
        );

    private async Task<int> Apply(ContentSegmentWritePlan plan)
    {
        await using MediaContext context = new(_options);
        return await context.ApplySegmentPlansAsync([plan], CancellationToken.None);
    }

    private ContentSegment Insert(string source, ContentSegmentType type, int episodeId = EpisodeId)
    {
        ContentSegment row = Row(source, type, episodeId);
        using MediaContext context = new(_options);
        context.ContentSegments.Add(row);
        context.SaveChanges();
        return new()
        {
            Id = row.Id,
            EpisodeId = row.EpisodeId,
            Source = row.Source,
            SegmentType = row.SegmentType,
            StartSeconds = row.StartSeconds,
            EndSeconds = row.EndSeconds,
            Confidence = row.Confidence,
        };
    }

    private List<ContentSegment> Rows()
    {
        using MediaContext context = new(_options);
        return [.. context.ContentSegments.AsNoTracking().Where(r => r.EpisodeId == EpisodeId)];
    }

    private static ContentSegment Row(
        string source,
        ContentSegmentType type,
        int episodeId = EpisodeId
    ) =>
        new()
        {
            EpisodeId = episodeId,
            Source = source,
            SegmentType = type,
            StartSeconds = type == ContentSegmentType.Intro ? 5 : 1280,
            EndSeconds = type == ContentSegmentType.Intro ? 90 : 1370,
            Confidence = 1.0,
        };
}
