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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Users;
using NoMercy.NmSystem.Domain;
using NoMercy.Tests.Repositories.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public class UserDataRepositoryPlaybackPreferenceTests : IDisposable
{
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly UserDataRepository _repository;

    public UserDataRepositoryPlaybackPreferenceTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _repository = new(_factory);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static PlaybackPreference Preference(string language, int? movieId = null) =>
        new()
        {
            UserId = SeedConstants.UserId,
            MovieId = movieId,
            Audio = new IAudio { Language = language },
        };

    private async Task<List<PlaybackPreference>> RowsAsync()
    {
        await using MediaContext context = _factory.CreateDbContext();
        return await context
            .PlaybackPreferences.AsNoTracking()
            .Where(p => p.UserId == SeedConstants.UserId)
            .ToListAsync();
    }

    [Fact]
    public async Task SavePlaybackPreference_SameMovieTwice_UpdatesOneRow()
    {
        await _repository.SavePlaybackPreferenceAsync(
            Preference("eng", movieId: 129),
            MediaTypes.MovieMediaType
        );
        await _repository.SavePlaybackPreferenceAsync(
            Preference("jpn", movieId: 129),
            MediaTypes.MovieMediaType
        );

        List<PlaybackPreference> rows = await RowsAsync();

        rows.Should().ContainSingle();
        rows[0].Audio!.Language.Should().Be("jpn");
    }

    [Fact]
    public async Task SaveLibraryPreferenceIfMissing_StoresOnceForTheLibraryType()
    {
        await _repository.SaveLibraryPreferenceIfMissingAsync(Preference("eng"), "movie");
        await _repository.SaveLibraryPreferenceIfMissingAsync(Preference("jpn"), "movie");

        List<PlaybackPreference> rows = await RowsAsync();

        rows.Should().ContainSingle();
        rows[0].LibraryId.Should().Be(SeedConstants.MovieLibraryId);
        rows[0].Audio!.Language.Should().Be("eng");
    }
}
