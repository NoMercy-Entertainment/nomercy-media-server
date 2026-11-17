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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Segments;
using NoMercy.MediaProcessing.Segments;
using NoMercy.NmSystem.Domain;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>
/// The writer and repository against a real SQLite database: stored chapters
/// in, ContentSegment rows out, under the precedence order.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ChapterSegmentWriterTests : IDisposable
{
    private const int AnimeEpisodeId = 101;
    private const int TvEpisodeId = 201;
    private const int MovieId = 301;

    private static readonly Ulid AnimeLibraryId = Ulid.NewUlid();
    private static readonly Ulid TvLibraryId = Ulid.NewUlid();

    // Hellsing S01E01 as the scan stores it: "Start" stub, OP, story "Intro", ED + Preview.
    private static readonly IChapter[] Hellsing =
    [
        Chapter(0, 0, 4800, "Start"),
        Chapter(1, 4800, 83061, "OP"),
        Chapter(2, 83061, 310438, "Intro"),
        Chapter(3, 310438, 686900, "Part A"),
        Chapter(4, 686900, 1282534, "Part B"),
        Chapter(5, 1282534, 1363824, "ED"),
        Chapter(6, 1363824, 1380128, "Preview"),
    ];

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly ChapterSegmentRepository _repository;
    private readonly ChapterSegmentWriter _writer;

    public ChapterSegmentWriterTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using (MediaContext context = new(_options))
        {
            context.Database.EnsureCreated();
            Seed(context);
        }

        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new MediaContext(_options));

        _repository = new(factory.Object);
        _writer = Writer(_repository);
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task An_encoded_episode_gets_its_chapter_segments()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);

        int changed = await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);

        changed.Should().Be(1);
        List<ContentSegment> rows = Segments(AnimeEpisodeId);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => r.Source == ContentSegmentSource.Chapters && r.Confidence == 1.0);
        rows.Should().ContainSingle(r => r.SegmentType == ContentSegmentType.Intro && r.StartSeconds == 4.8 && r.EndSeconds == 83.061);
        rows.Should().ContainSingle(r => r.SegmentType == ContentSegmentType.Outro && r.StartSeconds == 1282.534 && r.EndSeconds == 1380.128);
    }

    [Fact]
    public async Task A_rerun_with_the_same_chapters_writes_nothing()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);
        List<Ulid> before = [.. Segments(AnimeEpisodeId).Select(r => r.Id)];

        int changed = await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);

        changed.Should().Be(0);
        Segments(AnimeEpisodeId).Select(r => r.Id).Should().BeEquivalentTo(before);
    }

    [Fact]
    public async Task A_manual_intro_survives_and_blocks_the_chapter_intro()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        AddSegment(AnimeEpisodeId, ContentSegmentSource.Manual, ContentSegmentType.Intro, 0, 80);

        await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);

        List<ContentSegment> intros = [.. Segments(AnimeEpisodeId).Where(r => r.SegmentType == ContentSegmentType.Intro)];
        intros.Should().ContainSingle().Which.Source.Should().Be(ContentSegmentSource.Manual);
    }

    [Fact]
    public async Task A_chapter_title_replaces_the_detector_intro()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        AddSegment(AnimeEpisodeId, ContentSegmentSource.Detector, ContentSegmentType.Intro, 6, 85);

        await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);

        Segments(AnimeEpisodeId)
            .Where(r => r.SegmentType == ContentSegmentType.Intro)
            .Should()
            .ContainSingle()
            .Which.Source.Should()
            .Be(ContentSegmentSource.Chapters);
    }

    [Fact]
    public async Task Generic_chapters_of_a_non_anime_show_give_nothing_but_of_anime_an_opening()
    {
        IChapter[] generic =
        [
            Chapter(0, 0, 90000, "Chapter 1"),
            Chapter(1, 90000, 700000, "Chapter 2"),
            Chapter(2, 700000, 1300000, "Chapter 3"),
        ];
        AddFile(episodeId: AnimeEpisodeId, chapters: generic);
        AddFile(episodeId: TvEpisodeId, chapters: generic);

        await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);
        await _writer.ClassifyEpisodeAsync(TvEpisodeId, CancellationToken.None);

        Segments(AnimeEpisodeId)
            .Should()
            .ContainSingle()
            .Which.Confidence.Should()
            .Be(ContentSegmentPrecedence.ChapterLengthConfidence);
        Segments(TvEpisodeId).Should().BeEmpty();
    }

    [Fact]
    public async Task The_newest_video_file_of_an_episode_counts()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: [Chapter(0, 0, 600000, "Part A"), Chapter(1, 600000, 630000, "Credits")]);
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);

        await _writer.ClassifyEpisodeAsync(AnimeEpisodeId, CancellationToken.None);

        Segments(AnimeEpisodeId).Should().HaveCount(2);
    }

    [Fact]
    public async Task A_movie_is_classified_by_title()
    {
        AddFile(movieId: MovieId, chapters: [Chapter(0, 0, 5000000, "Feature"), Chapter(1, 5000000, 5300000, "End Credits")]);

        await _writer.ClassifyMovieAsync(MovieId, CancellationToken.None);

        using MediaContext context = new(_options);
        context
            .ContentSegments.AsNoTracking()
            .Should()
            .ContainSingle(r => r.MovieId == MovieId && r.EpisodeId == null && r.SegmentType == ContentSegmentType.Outro && r.StartSeconds == 5000);
    }

    [Fact]
    public async Task The_backfill_visits_every_item_with_chapters_and_clears_chapters_that_are_gone()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        AddFile(episodeId: TvEpisodeId, chapters: null);
        AddSegment(TvEpisodeId, ContentSegmentSource.Chapters, ContentSegmentType.Intro, 0, 30);
        AddSegment(TvEpisodeId, ContentSegmentSource.Detector, ContentSegmentType.Outro, 1200, 1260);

        int changed = await _writer.ClassifyAllAsync(CancellationToken.None);

        changed.Should().Be(2);
        Segments(AnimeEpisodeId).Should().HaveCount(2);
        Segments(TvEpisodeId)
            .Should()
            .ContainSingle()
            .Which.Source.Should()
            .Be(ContentSegmentSource.Detector);
        (await _writer.ClassifyAllAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task One_conflicting_item_does_not_stop_the_sweep()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        AddFile(episodeId: TvEpisodeId, chapters: Hellsing);
        ConflictingChapterSegmentRepository conflicting = new(_repository, failingEpisodeId: TvEpisodeId);

        int changed = await Writer(conflicting).ClassifyAllAsync(CancellationToken.None);

        changed.Should().Be(1);
        Segments(AnimeEpisodeId).Should().HaveCount(2);
        Segments(TvEpisodeId).Should().BeEmpty();
        conflicting.Failures.Should().Be(2, "the batch fails once, then the item once more");
    }

    [Fact]
    public async Task A_batch_that_conflicts_once_is_saved_item_by_item()
    {
        AddFile(episodeId: AnimeEpisodeId, chapters: Hellsing);
        AddFile(episodeId: TvEpisodeId, chapters: Hellsing);
        ConflictingChapterSegmentRepository conflicting = new(_repository, failingEpisodeId: TvEpisodeId, failOnce: true);

        int changed = await Writer(conflicting).ClassifyAllAsync(CancellationToken.None);

        changed.Should().Be(2);
        Segments(AnimeEpisodeId).Should().HaveCount(2);
        Segments(TvEpisodeId).Should().HaveCount(2);
    }

    private static ChapterSegmentWriter Writer(IChapterSegmentRepository repository) =>
        new(repository, new ChapterSegmentClassifier(), NullLogger<ChapterSegmentWriter>.Instance);

    private static void Seed(MediaContext context)
    {
        context.Libraries.AddRange(
            new Library { Id = AnimeLibraryId, Title = "Anime", Type = MediaTypes.AnimeMediaType },
            new Library { Id = TvLibraryId, Title = "TV", Type = MediaTypes.TvMediaType }
        );
        context.Tvs.AddRange(
            new Tv { Id = 1, Title = "Hellsing", LibraryId = AnimeLibraryId },
            new Tv { Id = 2, Title = "Rick and Morty", LibraryId = TvLibraryId }
        );
        context.Episodes.AddRange(
            new Episode { Id = AnimeEpisodeId, TvId = 1, SeasonId = 1, EpisodeNumber = 1, SeasonNumber = 1 },
            new Episode { Id = TvEpisodeId, TvId = 2, SeasonId = 2, EpisodeNumber = 1, SeasonNumber = 1 }
        );
        context.Movies.Add(new Movie { Id = MovieId, Title = "A Movie", LibraryId = TvLibraryId });
        context.SaveChanges();
    }

    private void AddFile(int? episodeId = null, int? movieId = null, IChapter[]? chapters = null)
    {
        using MediaContext context = new(_options);
        Ulid fileId = Ulid.NewUlid();
        Metadata metadata = new()
        {
            Filename = $"{fileId}.m3u8",
            HostFolder = $"/media/{fileId}",
            Chapters = chapters is null ? null : [.. chapters],
        };
        context.Metadata.Add(metadata);
        context.VideoFiles.Add(
            new VideoFile
            {
                Id = fileId,
                Filename = metadata.Filename,
                HostFolder = metadata.HostFolder,
                Folder = metadata.HostFolder,
                EpisodeId = episodeId,
                MovieId = movieId,
                MetadataId = metadata.Id,
            }
        );
        context.SaveChanges();
    }

    private void AddSegment(int episodeId, string source, ContentSegmentType type, double start, double end)
    {
        using MediaContext context = new(_options);
        context.ContentSegments.Add(
            new ContentSegment
            {
                EpisodeId = episodeId,
                Source = source,
                SegmentType = type,
                StartSeconds = start,
                EndSeconds = end,
                Confidence = 1.0,
            }
        );
        context.SaveChanges();
    }

    private List<ContentSegment> Segments(int episodeId)
    {
        using MediaContext context = new(_options);
        return [.. context.ContentSegments.AsNoTracking().Where(r => r.EpisodeId == episodeId)];
    }

    private static IChapter Chapter(int id, int startMs, int endMs, string title) =>
        new()
        {
            Id = id,
            StartTime = startMs,
            EndTime = endMs,
            Title = title,
        };
}
