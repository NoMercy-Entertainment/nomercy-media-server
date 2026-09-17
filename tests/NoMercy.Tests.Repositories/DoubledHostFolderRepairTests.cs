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
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Storage;

namespace NoMercy.Tests.Repositories;

// The rows this sweep rewrites are the ones an older importer wrote with the
// album folder inside HostFolder twice: the folder with forward slashes, a
// separator, then the same folder with backslashes. Filename stays the bare
// file name, so every consumer that combines the two builds a path that exists
// nowhere and the track is unplayable and unanalysable. A rescan does not
// repair it, and the halves are only safe to collapse when the file is really
// where the repaired folder says it is.
[Trait("Category", "Unit")]
public class DoubledHostFolderRepairTests : IDisposable
{
    private const string AlbumFolder = "Q:/Music/Nine Vaults/Paper Lanterns";
    private const string DoubledAlbumFolder =
        @"Q:/Music/Nine Vaults/Paper Lanterns\Q:\Music\Nine Vaults\Paper Lanterns";
    private const string TrackFile = "/03. Paper Lanterns.flac";

    private static readonly Ulid DriverId = Ulid.NewUlid();
    private static readonly Ulid FolderId = Ulid.NewUlid();

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public DoubledHostFolderRepairTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext ctx = new(_options);
        ctx.Database.EnsureCreated();

        ctx.Drivers.Add(
            new()
            {
                Id = DriverId,
                Name = "Media",
                Type = "local",
            }
        );
        ctx.Folders.Add(
            new()
            {
                Id = FolderId,
                DriverId = DriverId,
                Path = "Libraries/Music",
            }
        );
        ctx.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// The driver the sweep asks "is the file there?": it combines with a
    /// single '/' and says yes only for the paths named.
    /// </summary>
    private static Mock<IStorageDriver> Driver(params string[] existingPaths)
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d => d.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns<string, string[]>(
                (parent, segments) =>
                    parent.TrimEnd('/')
                    + string.Concat(segments.Select(segment => "/" + segment.TrimStart('/')))
            );
        driver.Setup(d => d.FileExists(It.IsAny<string>())).Returns<string>(existingPaths.Contains);
        return driver;
    }

    private DoubledHostFolderRepair BuildRepair(Mock<IStorageDriver> driver) =>
        new(
            new TestDbContextFactory(_options),
            driver.Object,
            NullLogger<DoubledHostFolderRepair>.Instance
        );

    private async Task AddTrack(string? hostFolder, string? filename = TrackFile)
    {
        await using MediaContext ctx = new(_options);
        ctx.Tracks.Add(
            new()
            {
                Id = Guid.NewGuid(),
                Name = "Paper Lanterns",
                HostFolder = hostFolder,
                Filename = filename,
                FolderId = FolderId,
                Duration = "03:12",
            }
        );
        await ctx.SaveChangesAsync();
    }

    private async Task<string?> ReadHostFolder()
    {
        await using MediaContext ctx = new(_options);
        return (await ctx.Tracks.AsNoTracking().SingleAsync()).HostFolder;
    }

    [Fact]
    public async Task Collapses_a_doubled_host_folder_when_the_file_is_there()
    {
        await AddTrack(DoubledAlbumFolder);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(1);
        (await ReadHostFolder()).Should().Be(AlbumFolder);
    }

    /// <summary>
    /// Nothing at the repaired path means the guess is unproven, and a row
    /// pointing at a path nobody checked is worse than the broken value: the
    /// broken one is at least recognisable.
    /// </summary>
    [Fact]
    public async Task Leaves_a_doubled_host_folder_alone_when_the_file_is_not_there()
    {
        await AddTrack(DoubledAlbumFolder);

        int repaired = await BuildRepair(Driver()).RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(DoubledAlbumFolder));
    }

    /// <summary>
    /// Two roots naming different folders is a value only a human can judge.
    /// </summary>
    [Fact]
    public async Task Leaves_a_second_root_alone_when_it_is_not_a_copy_of_the_first()
    {
        const string mismatched = @"Q:/Music/Nine Vaults/Paper Lanterns\R:\Archive\Nine Vaults";
        await AddTrack(mismatched);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(mismatched));
    }

    [Theory]
    [InlineData(AlbumFolder)]
    [InlineData("//vault-01/music/Nine Vaults/Paper Lanterns")]
    [InlineData("music/Nine Vaults/Paper Lanterns")]
    public async Task Leaves_a_healthy_host_folder_alone(string hostFolder)
    {
        await AddTrack(hostFolder);

        int repaired = await BuildRepair(Driver(AlbumFolder + TrackFile))
            .RunAsync(CancellationToken.None);

        repaired.Should().Be(0);
        (await ReadHostFolder()).Should().Be(PathAsStored(hostFolder));
    }

    /// <summary>
    /// A repaired row carries no second root any more, so the next boot's sweep
    /// must not select it and must not touch the value again.
    /// </summary>
    [Fact]
    public async Task Is_idempotent()
    {
        await AddTrack(DoubledAlbumFolder);
        Mock<IStorageDriver> driver = Driver(AlbumFolder + TrackFile);

        (await BuildRepair(driver).RunAsync(CancellationToken.None)).Should().Be(1);
        (await BuildRepair(driver).RunAsync(CancellationToken.None)).Should().Be(0);

        (await ReadHostFolder()).Should().Be(AlbumFolder);
    }

    // Track.HostFolder normalises separators on the way in, so an untouched row
    // reads back with forward slashes whatever it was written with. The test
    // asserts "unchanged", not "byte-identical to the literal above".
    private static string PathAsStored(string hostFolder) => hostFolder.Replace('\\', '/');

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
