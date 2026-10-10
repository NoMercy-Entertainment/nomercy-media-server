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
using NoMercy.MediaProcessing.Shows;
using NoMercy.Providers.TMDB.Models.TV;

namespace NoMercy.Tests.MediaProcessing.Shows;

/// <summary>
/// A classifier lookup that fails (rate-limited, network error, unparsable body)
/// used to read as a confirmed "tv" verdict, so a run over hundreds of shows
/// flagged every one the lookup happened to fail for as a library mismatch — real
/// anime (Hunter x Hunter, Fruits Basket, Little Witch Academia) reported
/// side-by-side with genuine non-anime, with no way to tell which was which.
/// </summary>
public sealed class AnimeClassificationAuditServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public AnimeClassificationAuditServiceTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext seed = new(_options);
        seed.Database.EnsureCreated();

        Library animeLibrary = new()
        {
            Id = Ulid.NewUlid(),
            Title = "Anime",
            Type = "anime",
        };
        seed.Libraries.Add(animeLibrary);
        seed.Tvs.Add(
            new()
            {
                Id = 1,
                Title = "Rate Limited While Kitsu Was Down",
                LibraryId = animeLibrary.Id,
            }
        );
        seed.Tvs.Add(
            new()
            {
                Id = 2,
                Title = "Genuinely Misfiled",
                LibraryId = animeLibrary.Id,
            }
        );
        seed.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    private IDbContextFactory<MediaContext> CreateFactory()
    {
        Mock<IDbContextFactory<MediaContext>> mock = new();
        mock.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return mock.Object;
    }

    /// <summary>Classifier stub keyed by title — null return simulates an inconclusive lookup.</summary>
    private sealed class StubClassifier(Dictionary<string, string?> verdictsByTitle)
        : IMediaTypeClassifier
    {
        public Task<string?> ClassifyAsync(TmdbTvShowAppends show) =>
            ClassifyAsync(show.Name, null);

        public Task<string?> ClassifyAsync(
            string name,
            int? year,
            string[]? originCountry = null
        ) => Task.FromResult(verdictsByTitle.GetValueOrDefault(name));
    }

    [Fact]
    public async Task FindMismatchesAsync_InconclusiveLookup_IsNotReportedAsAMismatch()
    {
        StubClassifier classifier = new(
            new() { ["Rate Limited While Kitsu Was Down"] = null, ["Genuinely Misfiled"] = "tv" }
        );
        AnimeClassificationAuditService service = new(
            CreateFactory(),
            classifier,
            NullLogger<AnimeClassificationAuditService>.Instance
        );

        IReadOnlyList<AnimeClassificationMismatch> mismatches = await service.FindMismatchesAsync();

        mismatches.Should().ContainSingle(m => m.Title == "Genuinely Misfiled");
        mismatches.Should().NotContain(m => m.Title == "Rate Limited While Kitsu Was Down");
    }

    [Fact]
    public async Task FindMismatchesAsync_ConfirmedMatchingType_IsNotReportedAsAMismatch()
    {
        StubClassifier classifier = new(
            new()
            {
                ["Rate Limited While Kitsu Was Down"] = "anime",
                ["Genuinely Misfiled"] = "anime",
            }
        );
        AnimeClassificationAuditService service = new(
            CreateFactory(),
            classifier,
            NullLogger<AnimeClassificationAuditService>.Instance
        );

        IReadOnlyList<AnimeClassificationMismatch> mismatches = await service.FindMismatchesAsync();

        mismatches.Should().BeEmpty();
    }
}
