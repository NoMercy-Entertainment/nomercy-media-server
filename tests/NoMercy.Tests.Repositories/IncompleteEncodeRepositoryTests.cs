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
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Encoder;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Repositories")]
public class IncompleteEncodeRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IncompleteEncodeRepository _repository;

    public IncompleteEncodeRepositoryTests()
    {
        _context = TestMediaContextFactory.CreateSeededContext();
        _repository = new(_context);
    }

    public void Dispose() => _context.Dispose();

    private IncompleteEncode AddRow(long mediaId, DateTime? lastSeenAt = null)
    {
        IncompleteEncode row = new()
        {
            MediaId = mediaId,
            FolderId = SeedConstants.MovieFolderId.ToString(),
            Title = $"Movie {mediaId}",
            MissingRenditions = "1080p\n720p",
            AttemptsMade = 1,
            FirstSeenAt = DateTime.UtcNow,
            LastSeenAt = lastSeenAt ?? DateTime.UtcNow,
        };
        _context.IncompleteEncodes.Add(row);
        _context.SaveChanges();
        return row;
    }

    [Fact]
    public async Task GetAllAsync_OrdersByLastSeenAtDescending()
    {
        AddRow(1, DateTime.UtcNow.AddMinutes(-10));
        AddRow(2, DateTime.UtcNow);

        List<IncompleteEncode> rows = await _repository.GetAllAsync();

        rows.Should().HaveCount(2);
        rows[0].MediaId.Should().Be(2);
    }

    [Fact]
    public async Task FindAsync_ReturnsNull_WhenRowDoesNotExist()
    {
        (await _repository.FindAsync(999)).Should().BeNull();
    }

    [Fact]
    public async Task FindAsync_ReturnsTheRow_WhenItExists()
    {
        IncompleteEncode row = AddRow(129);

        IncompleteEncode? found = await _repository.FindAsync(row.Id);

        found.Should().NotBeNull();
        found!.MediaId.Should().Be(129);
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheRow()
    {
        IncompleteEncode row = AddRow(129);

        await _repository.RemoveAsync(row);

        (await _repository.FindAsync(row.Id)).Should().BeNull();
    }

    [Fact]
    public async Task RemoveAllAsync_DeletesEveryRow_AndReturnsTheCount()
    {
        AddRow(1);
        AddRow(2);
        AddRow(3);

        int removed = await _repository.RemoveAllAsync();

        removed.Should().Be(3);
        (await _repository.GetAllAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FindFolderLibraryAsync_ReturnsTheOwningLibrary()
    {
        FolderLibrary? folderLibrary = await _repository.FindFolderLibraryAsync(
            SeedConstants.MovieFolderId
        );

        folderLibrary.Should().NotBeNull();
        folderLibrary!.LibraryId.Should().Be(SeedConstants.MovieLibraryId);
    }

    [Fact]
    public async Task FindFolderLibraryAsync_ReturnsNull_ForAnUnknownFolder()
    {
        (await _repository.FindFolderLibraryAsync(Ulid.NewUlid())).Should().BeNull();
    }

    [Fact]
    public async Task FindVideoFileForMediaAsync_MatchesByMovieId()
    {
        VideoFile? videoFile = await _repository.FindVideoFileForMediaAsync(129);

        videoFile.Should().NotBeNull();
        videoFile!.Id.Should().Be(SeedConstants.MovieVideoFile1Id);
    }

    [Fact]
    public async Task FindVideoFileForMediaAsync_MatchesByEpisodeId()
    {
        VideoFile? videoFile = await _repository.FindVideoFileForMediaAsync(62085);

        videoFile.Should().NotBeNull();
        videoFile!.Id.Should().Be(SeedConstants.TvVideoFile1Id);
    }

    [Fact]
    public async Task FindVideoFileForMediaAsync_ReturnsNull_WhenNoFileMatches()
    {
        (await _repository.FindVideoFileForMediaAsync(int.MaxValue)).Should().BeNull();
    }
}
