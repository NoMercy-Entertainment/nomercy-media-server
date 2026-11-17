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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Storage;
using NoMercy.Database.Models.TvShows;

namespace NoMercy.Tests.Database.Migrations;

/// <summary>
/// Existing databases hold LibraryTv rows left behind when a show was
/// re-imported under another library (#105). Replayed the way a self-hosted
/// install sees the upgrade: only a link to a library other than Tvs.LibraryId
/// goes, only when the show keeps its own link, and never when one of the
/// show's files was found under that library.
/// </summary>
[Trait("Category", "Integration")]
public class PhantomLibraryTvLinksMigrationTests : IDisposable
{
    private const string PreviousMigration = "20261116162517_AddTrackStemEngine";

    private readonly string _databasePath = Path.Combine(
        Path.GetTempPath(),
        $"nm_phantom_librarytv_migration_{Guid.NewGuid():N}.db"
    );

    private readonly Ulid _driverId = Ulid.NewUlid();
    private readonly Ulid _animeLibraryId = Ulid.NewUlid();
    private readonly Ulid _tvLibraryId = Ulid.NewUlid();
    private readonly Ulid _animeFolderId = Ulid.NewUlid();
    private readonly Ulid _tvFolderId = Ulid.NewUlid();

    public void Dispose()
    {
        // Pooled handles keep the file locked on Windows past Dispose().
        SqliteConnection.ClearAllPools();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task LinkToAnotherLibrary_WithNoFileThere_IsRemoved_AndTheShowsOwnLinkStays()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 1, _animeLibraryId, _animeLibraryId, _tvLibraryId);
            AddEpisodeFile(oldContext, 1, _animeFolderId.ToString(), "/media/anime/Show 1/");
            AddShow(oldContext, 2, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_animeLibraryId), await LinksOfAsync(1));
        Assert.Equal(Sorted(_tvLibraryId), await LinksOfAsync(2));
    }

    [Fact]
    public async Task OnlyLink_ThatDisagreesWithTheShowsLibrary_IsKept()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 3, _animeLibraryId, _tvLibraryId);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_tvLibraryId), await LinksOfAsync(3));
    }

    [Fact]
    public async Task LinkToAnotherLibrary_WithAFileRecordedUnderItsFolder_IsKept()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 4, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            AddEpisodeFile(oldContext, 4, _animeFolderId.ToString(), "/somewhere/else/");
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_tvLibraryId, _animeLibraryId), await LinksOfAsync(4));
    }

    [Fact]
    public async Task LinkToAnotherLibrary_WithAFileUnderItsPath_IsKept_WhateverTheSlashesOrCase()
    {
        Ulid videoFileId;
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 5, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            videoFileId = AddEpisodeFile(oldContext, 5, string.Empty, "placeholder");
            await oldContext.SaveChangesAsync();

            // A row written before paths were normalised on save.
            string legacyHostFolder = @"\MEDIA\Anime\Show 5\Season 01\";
            string id = videoFileId.ToString();
            await oldContext.Database.ExecuteSqlAsync(
                $"UPDATE VideoFiles SET HostFolder = {legacyHostFolder} WHERE Id = {id}"
            );
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_tvLibraryId, _animeLibraryId), await LinksOfAsync(5));
    }

    [Fact]
    public async Task FileUnderASiblingFolderWithTheSamePrefix_IsNoEvidence()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 6, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            AddEpisodeFile(oldContext, 6, _tvFolderId.ToString(), "/media/anime-archive/Show 6/");
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_tvLibraryId), await LinksOfAsync(6));
    }

    [Fact]
    public async Task ShowFiledUnderALibraryThatNoLongerExists_KeepsEveryLink()
    {
        Ulid goneLibraryId = Ulid.NewUlid();
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 7, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            await oldContext.SaveChangesAsync();

            // Only reachable with foreign keys off, which is how the bulk
            // delete paths run: the show still points at a deleted library.
            string goneId = goneLibraryId.ToString();
            await oldContext.Database.OpenConnectionAsync();
            await oldContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = OFF");
            await oldContext.Database.ExecuteSqlAsync(
                $"UPDATE Tvs SET LibraryId = {goneId} WHERE Id = {7}"
            );
            await oldContext.Database.ExecuteSqlAsync(
                $"INSERT INTO LibraryTv (LibraryId, TvId, AddedBy) VALUES ({goneId}, {7}, {LibraryLinkOrigin.File})"
            );
            await oldContext.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON");
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(goneLibraryId, _tvLibraryId, _animeLibraryId), await LinksOfAsync(7));
    }

    [Fact]
    public async Task ShowWithOnlyItsOwnLink_IsUntouched()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 8, _tvLibraryId, _tvLibraryId);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_tvLibraryId), await LinksOfAsync(8));
    }

    [Fact]
    public async Task ManualPhantomLink_FoldsIntoTheOwnLink_WithTheEarliestDate()
    {
        DateTime ownAddedAt = new(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        DateTime phantomAddedAt = new(2024, 1, 1, 8, 30, 0, DateTimeKind.Utc);
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 20, _tvLibraryId);
            AddLink(oldContext, _tvLibraryId, 20, LibraryLinkOrigin.File, ownAddedAt);
            AddLink(oldContext, _animeLibraryId, 20, LibraryLinkOrigin.Manual, phantomAddedAt);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        LibraryTv link = Assert.Single(await LinkRowsOfAsync(20));
        Assert.Equal(_tvLibraryId, link.LibraryId);
        Assert.Equal(LibraryLinkOrigin.Manual, link.AddedBy);
        Assert.Equal(phantomAddedAt, link.AddedAt);
    }

    [Fact]
    public async Task OwnLinkWithTheEarlierDate_KeepsIt_AndAFilePhantomLeavesTheOriginAlone()
    {
        DateTime ownAddedAt = new(2023, 3, 3, 3, 3, 3, DateTimeKind.Utc);
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 21, _tvLibraryId);
            AddLink(oldContext, _tvLibraryId, 21, LibraryLinkOrigin.File, ownAddedAt);
            AddLink(
                oldContext,
                _animeLibraryId,
                21,
                LibraryLinkOrigin.File,
                new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            );
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        LibraryTv link = Assert.Single(await LinkRowsOfAsync(21));
        Assert.Equal(LibraryLinkOrigin.File, link.AddedBy);
        Assert.Equal(ownAddedAt, link.AddedAt);
    }

    [Fact]
    public async Task OwnLinkWithoutADate_TakesThePhantomsDate()
    {
        DateTime phantomAddedAt = new(2025, 6, 7, 8, 9, 10, DateTimeKind.Utc);
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 22, _tvLibraryId);
            AddLink(oldContext, _tvLibraryId, 22, LibraryLinkOrigin.File, null);
            AddLink(oldContext, _animeLibraryId, 22, LibraryLinkOrigin.File, phantomAddedAt);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        LibraryTv link = Assert.Single(await LinkRowsOfAsync(22));
        Assert.Equal(phantomAddedAt, link.AddedAt);
    }

    [Fact]
    public async Task OwnLinkAlreadyManual_StaysManual()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 23, _tvLibraryId);
            AddLink(oldContext, _tvLibraryId, 23, LibraryLinkOrigin.Manual, null);
            AddLink(oldContext, _animeLibraryId, 23, LibraryLinkOrigin.File, null);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        LibraryTv link = Assert.Single(await LinkRowsOfAsync(23));
        Assert.Equal(LibraryLinkOrigin.Manual, link.AddedBy);
        Assert.Null(link.AddedAt);
    }

    [Fact]
    public async Task ShowWithAFileBackedSecondLink_IsUntouched_OriginsAndDatesIncluded()
    {
        DateTime ownAddedAt = new(2026, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        DateTime secondAddedAt = new(2022, 2, 2, 2, 2, 2, DateTimeKind.Utc);
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 24, _tvLibraryId);
            AddLink(oldContext, _tvLibraryId, 24, LibraryLinkOrigin.File, ownAddedAt);
            AddLink(oldContext, _animeLibraryId, 24, LibraryLinkOrigin.Manual, secondAddedAt);
            AddEpisodeFile(oldContext, 24, _animeFolderId.ToString(), "/media/anime/Show 24/");
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        List<LibraryTv> links = await LinkRowsOfAsync(24);
        Assert.Equal(2, links.Count);
        LibraryTv own = links.Single(link => link.LibraryId == _tvLibraryId);
        Assert.Equal(LibraryLinkOrigin.File, own.AddedBy);
        Assert.Equal(ownAddedAt, own.AddedAt);
        LibraryTv second = links.Single(link => link.LibraryId == _animeLibraryId);
        Assert.Equal(LibraryLinkOrigin.Manual, second.AddedBy);
        Assert.Equal(secondAddedAt, second.AddedAt);
    }

    [Theory]
    [InlineData(@"D:\Anime\", "d:/anime/Show 30/Season 01/", true)]
    [InlineData(@"D:\Anime", @"D:\Anime 2\Show 30\", false)]
    [InlineData(@"\\NAS\Media\Anime", "//nas/media/anime/Show 30/", true)]
    [InlineData(@"\\NAS\Media\Anime", @"\\NAS\Media\Animation\Show 30\", false)]
    public async Task WindowsAndUncFolderPaths_CountAsEvidenceOnlyUnderTheFolder(
        string animeFolderPath,
        string hostFolder,
        bool kept
    )
    {
        Ulid videoFileId;
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 30, _tvLibraryId, _tvLibraryId, _animeLibraryId);
            videoFileId = AddEpisodeFile(oldContext, 30, string.Empty, "placeholder");
            await oldContext.SaveChangesAsync();

            // Raw paths as a Windows install stored them before saves
            // normalised the slashes.
            string folderId = _animeFolderId.ToString();
            string fileId = videoFileId.ToString();
            await oldContext.Database.ExecuteSqlAsync(
                $"UPDATE Folders SET Path = {animeFolderPath} WHERE Id = {folderId}"
            );
            await oldContext.Database.ExecuteSqlAsync(
                $"UPDATE VideoFiles SET HostFolder = {hostFolder} WHERE Id = {fileId}"
            );
        }

        await UpgradeAsync();

        List<Ulid> expected = kept ? Sorted(_tvLibraryId, _animeLibraryId) : Sorted(_tvLibraryId);
        Assert.Equal(expected, await LinksOfAsync(30));
    }

    [Fact]
    public async Task ShowWithNoEpisodesAtAll_LosesOnlyThePhantomLink()
    {
        await using (MediaContext oldContext = await OpenAtPreviousMigrationAsync())
        {
            AddShow(oldContext, 31, _animeLibraryId, _animeLibraryId, _tvLibraryId);
            await oldContext.SaveChangesAsync();
        }

        await UpgradeAsync();

        Assert.Equal(Sorted(_animeLibraryId), await LinksOfAsync(31));
    }

    private MediaContext OpenContext()
    {
        DbContextOptionsBuilder<MediaContext> builder = new();
        builder.UseSqlite($"Data Source={_databasePath}");

        return new(builder.Options);
    }

    private async Task<MediaContext> OpenAtPreviousMigrationAsync()
    {
        MediaContext context = OpenContext();
        await context.GetService<IMigrator>().MigrateAsync(PreviousMigration);

        context.Add(
            new Driver
            {
                Id = _driverId,
                Name = "local",
                Type = "local",
            }
        );
        context.Add(
            new Library
            {
                Id = _animeLibraryId,
                Title = "Anime",
                Type = "anime",
            }
        );
        context.Add(
            new Library
            {
                Id = _tvLibraryId,
                Title = "TV Shows",
                Type = "tv",
            }
        );
        context.Add(
            new Folder
            {
                Id = _animeFolderId,
                Path = "/media/anime/",
                DriverId = _driverId,
            }
        );
        context.Add(
            new Folder
            {
                Id = _tvFolderId,
                Path = "/media/tv",
                DriverId = _driverId,
            }
        );
        context.Add(new FolderLibrary(_animeFolderId, _animeLibraryId));
        context.Add(new FolderLibrary(_tvFolderId, _tvLibraryId));

        return context;
    }

    private async Task UpgradeAsync()
    {
        await using MediaContext upgradeContext = OpenContext();
        await upgradeContext.Database.MigrateAsync();
    }

    private async Task<List<Ulid>> LinksOfAsync(int tvId)
    {
        await using MediaContext readContext = OpenContext();
        List<Ulid> libraryIds = await readContext
            .LibraryTv.AsNoTracking()
            .Where(link => link.TvId == tvId)
            .Select(link => link.LibraryId)
            .ToListAsync();

        return [.. libraryIds.Order()];
    }

    private async Task<List<LibraryTv>> LinkRowsOfAsync(int tvId)
    {
        await using MediaContext readContext = OpenContext();
        return await readContext
            .LibraryTv.AsNoTracking()
            .Where(link => link.TvId == tvId)
            .ToListAsync();
    }

    private static List<Ulid> Sorted(params Ulid[] libraryIds) => [.. libraryIds.Order()];

    private static void AddLink(
        MediaContext context,
        Ulid libraryId,
        int tvId,
        string addedBy,
        DateTime? addedAt
    )
    {
        context.Add(new LibraryTv(libraryId, tvId, addedBy) { AddedAt = addedAt });
    }

    private static void AddShow(
        MediaContext context,
        int tvId,
        Ulid filedUnder,
        params Ulid[] linkedTo
    )
    {
        context.Add(
            new Tv
            {
                Id = tvId,
                Title = $"Show {tvId}",
                TitleSort = $"Show {tvId}",
                LibraryId = filedUnder,
            }
        );

        foreach (Ulid libraryId in linkedTo)
        {
            context.Add(new LibraryTv(libraryId, tvId));
        }
    }

    private static Ulid AddEpisodeFile(
        MediaContext context,
        int tvId,
        string share,
        string hostFolder
    )
    {
        int seasonId = tvId * 100;
        int episodeId = tvId * 1000;
        Ulid videoFileId = Ulid.NewUlid();

        context.Add(
            new Season
            {
                Id = seasonId,
                TvId = tvId,
                SeasonNumber = 1,
            }
        );
        context.Add(
            new Episode
            {
                Id = episodeId,
                TvId = tvId,
                SeasonId = seasonId,
                SeasonNumber = 1,
                EpisodeNumber = 1,
            }
        );
        context.Add(
            new VideoFile
            {
                Id = videoFileId,
                EpisodeId = episodeId,
                Filename = $"Show {tvId} S01E01.mkv",
                HostFolder = hostFolder,
                Folder = "/Show/Season 01",
                Quality = "1080p",
                Languages = "en",
                Share = share,
            }
        );

        return videoFileId;
    }
}
