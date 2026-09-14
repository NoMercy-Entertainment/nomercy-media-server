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
using NoMercy.Tests.Repositories.Infrastructure;
using Xunit;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Unit")]
public class VideoFileAccessTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly VideoFileRepository _repository;

    public VideoFileAccessTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateFactory();

        using MediaContext seedContext = _factory.CreateDbContext();
        TestMediaContextFactory.SeedData(seedContext);
        _repository = new(_factory);
    }

    [Fact]
    public async Task GetForUserWithMetadataAsync_ReturnsMovieAndEpisodeFilesTheUserCanReach()
    {
        VideoFile? movieFile = await _repository.GetForUserWithMetadataAsync(
            SeedConstants.MovieVideoFile1Id,
            SeedConstants.UserId
        );
        VideoFile? episodeFile = await _repository.GetForUserWithMetadataAsync(
            SeedConstants.TvVideoFile1Id,
            SeedConstants.UserId
        );

        movieFile.Should().NotBeNull();
        episodeFile.Should().NotBeNull();
    }

    [Fact]
    public async Task GetForUserWithMetadataAsync_IsNullForAnotherUserOrAnUnknownFile()
    {
        (
            await _repository.GetForUserWithMetadataAsync(
                SeedConstants.MovieVideoFile1Id,
                Guid.NewGuid()
            )
        )
            .Should()
            .BeNull();
        (await _repository.GetForUserWithMetadataAsync(Ulid.NewUlid(), SeedConstants.UserId))
            .Should()
            .BeNull();
    }

    [Fact]
    public async Task IsLibraryFolderAsync_KnowsTheSeededFolderOnly()
    {
        (await _repository.IsLibraryFolderAsync(SeedConstants.MovieFolderId)).Should().BeTrue();
        (await _repository.IsLibraryFolderAsync(Ulid.NewUlid())).Should().BeFalse();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
