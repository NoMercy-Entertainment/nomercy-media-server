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
using NoMercy.Api.DTOs.Dashboard;
using NoMercy.Data.DTOs;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Storage;
using NoMercy.NmSystem.Domain;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

[Trait("Category", "Characterization")]
public class LibraryRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly LibraryRepository _repository;
    private readonly SqliteConnection _factoryConnection;

    public LibraryRepositoryTests()
    {
        (IDbContextFactory<MediaContext> factory, _factoryConnection) =
            TestMediaContextFactory.CreateSeededFactory();
        _context = factory.CreateDbContext();
        _repository = new(factory);
    }

    [Fact]
    public async Task FindInboxFolderAsync_MatchesOnlyAnInboxFolder_IgnoringSlashesAndCase()
    {
        Ulid inboxLibraryId = Ulid.NewUlid();
        Ulid inboxFolderId = Ulid.NewUlid();
        _context.Libraries.Add(
            new Library
            {
                Id = inboxLibraryId,
                Title = "Inbox",
                Type = MediaTypes.InboxMediaType,
            }
        );
        _context.Folders.Add(
            new Folder
            {
                Id = inboxFolderId,
                Path = "D:\\Drop\\",
                DriverId = Driver.SystemLocalDriverId,
            }
        );
        _context.FolderLibrary.Add(new(inboxFolderId, inboxLibraryId));
        await _context.SaveChangesAsync();

        FolderLibrary? inbox = await _repository.FindInboxFolderAsync("d:/drop");
        FolderLibrary? movies = await _repository.FindInboxFolderAsync("/media/movies");

        Assert.NotNull(inbox);
        Assert.Equal(inboxLibraryId, inbox.LibraryId);
        Assert.Equal(inboxFolderId, inbox.Folder.Id);
        Assert.Null(movies);
    }

    [Fact]
    public async Task GetImportFailures_FiltersByLibraryAndResolvedState_NewestFirst()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        _context.ImportFailures.AddRange(
            new ImportFailure
            {
                LibraryId = SeedConstants.MovieLibraryId,
                FilePath = "/old.mkv",
                LastAttemptAt = now.AddHours(-2),
            },
            new ImportFailure
            {
                LibraryId = SeedConstants.MovieLibraryId,
                FilePath = "/new.mkv",
                LastAttemptAt = now,
            },
            new ImportFailure
            {
                LibraryId = SeedConstants.MovieLibraryId,
                FilePath = "/fixed.mkv",
                LastAttemptAt = now.AddHours(-1),
                Resolved = true,
            },
            new ImportFailure
            {
                LibraryId = SeedConstants.TvLibraryId,
                FilePath = "/other-library.mkv",
                LastAttemptAt = now,
            }
        );
        await _context.SaveChangesAsync();

        List<ImportFailure> all = await _repository.GetImportFailuresAsync(
            SeedConstants.MovieLibraryId,
            null
        );
        List<ImportFailure> open = await _repository.GetImportFailuresAsync(
            SeedConstants.MovieLibraryId,
            false
        );

        Assert.Equal(["/new.mkv", "/fixed.mkv", "/old.mkv"], all.Select(f => f.FilePath));
        Assert.Equal(["/new.mkv", "/old.mkv"], open.Select(f => f.FilePath));
    }

    [Fact]
    public async Task GetLibraries_ReturnsLibrariesForUser()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.UserId);

        Assert.Equal(2, libraries.Count);
        Assert.Contains(libraries, l => l.Title == "Movies");
        Assert.Contains(libraries, l => l.Title == "TV Shows");
    }

    [Fact]
    public async Task GetLibraries_ReturnsEmpty_WhenUserHasNoAccess()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.OtherUserId);

        Assert.Empty(libraries);
    }

    [Fact]
    public async Task GetLibraries_OrderedByOrder()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.UserId);

        Assert.Equal("Movies", libraries[0].Title);
        Assert.Equal("TV Shows", libraries[1].Title);
    }

    [Fact]
    public async Task GetLibraryByIdAsync_Ulid_ReturnsLibrary()
    {
        Library? library = await _repository.GetLibraryByIdAsync(SeedConstants.MovieLibraryId);

        Assert.NotNull(library);
        Assert.Equal("Movies", library.Title);
    }

    [Fact]
    public async Task GetLibraryByIdAsync_Ulid_ReturnsNull_WhenNotFound()
    {
        Library? library = await _repository.GetLibraryByIdAsync(Ulid.NewUlid());

        Assert.Null(library);
    }

    [Fact]
    public async Task GetAllLibrariesAsync_ReturnsAllLibraries()
    {
        List<Library> libraries = await _repository.GetAllLibrariesAsync();

        Assert.Equal(2, libraries.Count);
    }

    [Fact]
    public async Task GetFoldersAsync_ReturnsFolders()
    {
        List<FolderDto> folders = await _repository.GetFoldersAsync();

        Assert.NotEmpty(folders);
    }

    [Fact]
    public async Task GetLibraryMovieCardsAsync_ReturnsMovieCards()
    {
        List<MovieCardDto> cards = await _repository.GetLibraryMovieCardsAsync(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "US",
            10,
            0
        );

        Assert.Equal(2, cards.Count);
        Assert.Contains(cards, c => c.Title == "Spirited Away");
        Assert.Contains(cards, c => c.Title == "Pulp Fiction");
    }

    [Fact]
    public async Task GetLibraryMovieCardsAsync_RespectsSkipAndTake()
    {
        List<MovieCardDto> cards = await _repository.GetLibraryMovieCardsAsync(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "US",
            1,
            0
        );

        Assert.Single(cards);
    }

    [Fact]
    public async Task GetLibraryMovieCardsAsync_ReturnsEmpty_WhenUserHasNoAccess()
    {
        List<MovieCardDto> cards = await _repository.GetLibraryMovieCardsAsync(
            SeedConstants.OtherUserId,
            SeedConstants.MovieLibraryId,
            "US",
            10,
            0
        );

        Assert.Empty(cards);
    }

    [Fact]
    public async Task GetLibraryTvCardsAsync_ReturnsTvCards()
    {
        List<TvCardDto> cards = await _repository.GetLibraryTvCardsAsync(
            SeedConstants.UserId,
            SeedConstants.TvLibraryId,
            "US",
            10,
            0
        );

        Assert.Single(cards);
        Assert.Equal("Breaking Bad", cards[0].Title);
    }

    [Fact]
    public async Task GetLibraryMovieCardsAsync_TakeMatchesCarouselSize()
    {
        // Verify that Take limits results to the requested carousel size
        List<MovieCardDto> allCards = await _repository.GetLibraryMovieCardsAsync(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "US",
            100,
            0
        );
        Assert.Equal(2, allCards.Count);

        List<MovieCardDto> limitedCards = await _repository.GetLibraryMovieCardsAsync(
            SeedConstants.UserId,
            SeedConstants.MovieLibraryId,
            "US",
            1,
            0
        );
        Assert.Single(limitedCards);
    }

    [Fact]
    public async Task GetLibraryTvCardsAsync_TakeMatchesCarouselSize()
    {
        List<TvCardDto> allCards = await _repository.GetLibraryTvCardsAsync(
            SeedConstants.UserId,
            SeedConstants.TvLibraryId,
            "US",
            100,
            0
        );
        Assert.Single(allCards);

        List<TvCardDto> limitedCards = await _repository.GetLibraryTvCardsAsync(
            SeedConstants.UserId,
            SeedConstants.TvLibraryId,
            "US",
            1,
            0
        );
        Assert.Single(limitedCards);
    }

    [Fact]
    public async Task GetLibraryByIdAsync_Paginated_TakeLimitsMoviesPerCarousel()
    {
        // The .Take(take) inside Include() limits movies per-carousel
        Library? library = await _repository.GetLibraryByIdAsync(
            SeedConstants.MovieLibraryId,
            SeedConstants.UserId,
            "en",
            "US",
            1,
            0
        );

        Assert.NotNull(library);
        Assert.Single(library.LibraryMovies);
    }

    [Fact]
    public async Task GetLibraryByIdAsync_Paginated_TakeIsOrderedByTitleSort_NotInsertionOrder()
    {
        // "Spirited Away" (Id 129) is seeded before "Pulp Fiction" (Id 680), so a Take(1)
        // without an explicit OrderBy would return whatever the database's unordered scan
        // happens to yield first (observed: insertion order) instead of the alphabetically
        // first title. EF Core's Include(...).Take(n) requires its own OrderBy inside that
        // navigation lambda — an outer OrderBy on the root query does not cover it.
        Library? library = await _repository.GetLibraryByIdAsync(
            SeedConstants.MovieLibraryId,
            SeedConstants.UserId,
            "en",
            "US",
            1,
            0
        );

        Assert.NotNull(library);
        Assert.Single(library.LibraryMovies);
        Assert.Equal("Pulp Fiction", library.LibraryMovies.Single().Movie.Title);
    }

    [Fact]
    public async Task GetLibraryByIdAsync_Paginated_TakeReturnsAllWhenHigherThanCount()
    {
        Library? library = await _repository.GetLibraryByIdAsync(
            SeedConstants.MovieLibraryId,
            SeedConstants.UserId,
            "en",
            "US",
            100,
            0
        );

        Assert.NotNull(library);
        Assert.Equal(2, library.LibraryMovies.Count);
    }

    [Fact]
    public async Task AddLibraryAsync_CreatesLibrary()
    {
        Ulid newLibraryId = Ulid.NewUlid();
        Library newLibrary = new()
        {
            Id = newLibraryId,
            Title = "Music",
            Type = "music",
            Order = 3,
        };

        await _repository.AddLibraryAsync(newLibrary, SeedConstants.UserId);

        Library? found = await _repository.GetLibraryByIdAsync(newLibraryId);
        Assert.NotNull(found);
        Assert.Equal("Music", found.Title);
    }

    [Fact]
    public async Task DeleteLibraryAsync_RemovesLibrary()
    {
        Library? library = await _context.Libraries.FirstOrDefaultAsync(l =>
            l.Id == SeedConstants.MovieLibraryId
        );
        Assert.NotNull(library);

        await _repository.DeleteLibraryAsync(library);

        Library? deleted = await _repository.GetLibraryByIdAsync(SeedConstants.MovieLibraryId);
        Assert.Null(deleted);
    }

    [Fact]
    public async Task GetLibraries_IncludesEncoderProfilesOnFolders()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.UserId);

        Library movieLibrary = libraries.First(l => l.Title == "Movies");
        Assert.NotEmpty(movieLibrary.FolderLibraries);

        FolderLibrary folderLibrary = movieLibrary.FolderLibraries.First();
        Assert.NotNull(folderLibrary.Folder);
        Assert.NotEmpty(folderLibrary.Folder.EncodingPresetFolders);

        EncodingPresetFolder link = folderLibrary.Folder.EncodingPresetFolders.First();
        Assert.NotNull(link.Preset);
        Assert.Equal("Default HLS", link.Preset!.Name);
    }

    [Fact]
    public async Task GetLibraries_MapsToLibrariesResponseItemDto_WithoutException()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.UserId);

        // This is exactly what the controller does - it should not throw
        List<LibrariesResponseItemDto> response =
        [
            .. libraries.Select(library => new LibrariesResponseItemDto(library)),
        ];

        Assert.Equal(2, response.Count);

        LibrariesResponseItemDto movieDto = response.First(r => r.Title == "Movies");
        Assert.NotEmpty(movieDto.FolderLibrary);
        Assert.NotEmpty(movieDto.FolderLibrary[0].Folder.EncoderProfiles);
        Assert.Equal("Default HLS", movieDto.FolderLibrary[0].Folder.EncoderProfiles[0].Name);
    }

    [Fact]
    public async Task GetFoldersAsync_MapsFolderDto_WithEncoderProfiles()
    {
        List<FolderDto> folders = await _repository.GetFoldersAsync();

        Assert.NotEmpty(folders);
        // FolderDto uses Select projection so EncodingPresetFolders may not be loaded
        // in the projection query, but should not throw
    }

    [Fact]
    public async Task GetLibraries_IncludesLanguageLibraries()
    {
        List<Library> libraries = await _repository.GetLibraries(SeedConstants.UserId);

        Library movieLibrary = libraries.First(l => l.Title == "Movies");
        Assert.NotEmpty(movieLibrary.LanguageLibraries);
        Assert.Equal("en", movieLibrary.LanguageLibraries.First().Language.Iso6391);
    }

    [Fact]
    public async Task SyncEncodingPresetFolderAsync_ReplacesOldMappingWithNew()
    {
        EncodingPreset newPreset = new()
        {
            Id = Ulid.NewUlid(),
            Name = "New Profile",
            ProfileJson = "{}",
            IsBuiltIn = false,
        };
        _context.EncodingPresets.Add(newPreset);
        await _context.SaveChangesAsync();

        Folder folder = await _context.Folders.FirstAsync(f => f.Id == SeedConstants.MovieFolderId);

        List<EncodingPresetFolder> newMappings =
        [
            new() { PresetId = newPreset.Id, FolderId = SeedConstants.MovieFolderId },
        ];

        await _repository.SyncEncodingPresetFolderAsync(newMappings, [folder]);

        List<EncodingPresetFolder> stored = await _context
            .EncodingPresetFolders.Where(link => link.FolderId == SeedConstants.MovieFolderId)
            .ToListAsync();

        Assert.Single(stored);
        Assert.Equal(newPreset.Id, stored[0].PresetId);
        Assert.DoesNotContain(stored, link => link.PresetId == SeedConstants.EncodingPresetId);
    }

    [Fact]
    public async Task SyncEncodingPresetFolderAsync_RollsBackDeleteWhenInsertFails()
    {
        Folder folder = await _context.Folders.FirstAsync(f => f.Id == SeedConstants.MovieFolderId);

        // Non-existent PresetId violates the FK on upsert, so the whole sync
        // (delete + insert) must roll back as one unit — the original
        // mapping seeded in SeedData must survive.
        List<EncodingPresetFolder> invalidMappings =
        [
            new() { PresetId = Ulid.NewUlid(), FolderId = SeedConstants.MovieFolderId },
        ];

        await Assert.ThrowsAnyAsync<Exception>(() =>
            _repository.SyncEncodingPresetFolderAsync(invalidMappings, [folder])
        );

        EncodingPresetFolder? original = await _context.EncodingPresetFolders.FirstOrDefaultAsync(
            link =>
                link.FolderId == SeedConstants.MovieFolderId
                && link.PresetId == SeedConstants.EncodingPresetId
        );

        Assert.NotNull(original);
    }

    [Fact]
    public async Task UpdateLibraryAsync_Succeeds_WhenTwoFoldersShareOneDriver()
    {
        Folder secondFolder = new()
        {
            Id = Ulid.NewUlid(),
            Path = "/media/movies-extra",
            DriverId = Driver.SystemLocalDriverId,
        };
        _context.Folders.Add(secondFolder);
        _context.FolderLibrary.Add(new(secondFolder.Id, SeedConstants.MovieLibraryId));
        await _context.SaveChangesAsync();

        Library? library = await _repository.GetLibraryByIdAsync(SeedConstants.MovieLibraryId);
        Assert.NotNull(library);
        library.Title = "Renamed Movies";

        await _repository.UpdateLibraryAsync(library);

        Library? reloaded = await _repository.GetLibraryByIdLiteAsync(SeedConstants.MovieLibraryId);
        Assert.NotNull(reloaded);
        Assert.Equal("Renamed Movies", reloaded.Title);
    }

    [Fact]
    public async Task UpdateLibraryAsync_DoesNotRewriteFoldersOrDrivers()
    {
        Library? library = await _repository.GetLibraryByIdAsync(SeedConstants.MovieLibraryId);
        Assert.NotNull(library);

        library.Title = "Retitled";
        library.FolderLibraries.First().Folder.Path = "/tampered/path";
        library.FolderLibraries.First().Folder.Driver!.Name = "Tampered Driver";

        await _repository.UpdateLibraryAsync(library);

        _context.ChangeTracker.Clear();

        Folder folder = await _context.Folders.FirstAsync(f => f.Id == SeedConstants.MovieFolderId);
        Driver driver = await _context.Drivers.FirstAsync(d => d.Id == Driver.SystemLocalDriverId);

        Assert.Equal("/media/movies", folder.Path);
        Assert.Equal("Local Filesystem", driver.Name);
    }

    [Fact]
    public async Task SetLibraryLanguagesAsync_RemovesLanguagesNotInTheRequestedSet()
    {
        await _repository.SetLibraryLanguagesAsync(SeedConstants.MovieLibraryId, []);

        _context.ChangeTracker.Clear();

        List<LanguageLibrary> remaining = await _context
            .LanguageLibrary.Where(ll => ll.LibraryId == SeedConstants.MovieLibraryId)
            .ToListAsync();

        Assert.Empty(remaining);
    }

    [Fact]
    public async Task SetLibraryLanguagesAsync_AddsRequestedLanguage_AndIsIdempotent()
    {
        await _repository.SetLibraryLanguagesAsync(SeedConstants.MovieLibraryId, [1]);
        await _repository.SetLibraryLanguagesAsync(SeedConstants.MovieLibraryId, [1]);

        _context.ChangeTracker.Clear();

        List<LanguageLibrary> remaining = await _context
            .LanguageLibrary.Where(ll => ll.LibraryId == SeedConstants.MovieLibraryId)
            .ToListAsync();

        Assert.Single(remaining);
        Assert.Equal(1, remaining[0].LanguageId);
    }

    public void Dispose()
    {
        _context.Database.EnsureDeleted();
        _context.Dispose();
        _factoryConnection.Dispose();
    }
}
