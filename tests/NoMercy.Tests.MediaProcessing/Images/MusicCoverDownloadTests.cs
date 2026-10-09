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
using NoMercy.Data.Jobs;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Images;
using NoMercy.Providers.CoverArt.Models;
using NoMercy.Providers.FanArt.Models;

namespace NoMercy.Tests.MediaProcessing.Images;

/// <summary>
/// A cover name was written into the database straight from a FanArt or Cover
/// Art Archive URL, without the file ever being downloaded, so the image
/// answered 404 for good. Every writer now stores the file first and writes the
/// name only for a file that is really there.
/// </summary>
[Trait("Category", "Unit")]
public sealed class MusicCoverDownloadTests : IDisposable
{
    private const string FanArtHost = "https://assets.fanart.tv/fanart/music";
    private const string CoverArtHost = "https://coverartarchive.org/release";

    private readonly SqliteConnection _connection;
    private readonly MediaContext _context;

    public MusicCoverDownloadTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }

        DbContextOptions<MediaContext> options = new DbContextOptionsBuilder<MediaContext>()
            .UseSqlite(_connection)
            .Options;
        _context = new(options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private sealed class StubCoverFiles(
        IEnumerable<string> downloadable,
        IEnumerable<string>? stored = null
    ) : MusicCoverFiles
    {
        private readonly HashSet<string> _downloadable = [.. downloadable];
        private readonly HashSet<string> _stored = [.. stored ?? []];

        public List<string> Attempted { get; } = [];

        public override Task<bool> IsStoredAsync(string cover) =>
            Task.FromResult(_stored.Contains(Path.GetFileName(cover)));

        protected override Task<bool> DownloadFanArtAsync(Uri url) => Download(url);

        protected override Task<bool> DownloadCoverArtAsync(Uri url) => Download(url);

        private Task<bool> Download(Uri url)
        {
            string fileName = Path.GetFileName(url.LocalPath);
            Attempted.Add(fileName);
            return Task.FromResult(_downloadable.Contains(fileName));
        }
    }

    private static NoMercy.Providers.FanArt.Models.Image FanArtImage(string fileName) =>
        new() { Url = new($"{FanArtHost}/{Guid.NewGuid()}/{fileName}") };

    private static CoverArtImage FrontCover(string fileName) =>
        new()
        {
            Types = ["Front"],
            CoverArtThumbnails = new()
            {
                Large = new($"{CoverArtHost}/{Guid.NewGuid()}/{fileName}"),
            },
        };

    private async Task<Artist> SeedArtistAsync(string? cover)
    {
        Artist artist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Artist",
            Folder = "/artist",
            HostFolder = "/artist",
            Cover = cover,
        };
        _context.Artists.Add(artist);
        await _context.SaveChangesAsync();
        return artist;
    }

    private async Task<(ReleaseGroup Group, Album Album)> SeedReleaseAsync(string? cover)
    {
        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Album",
            Cover = cover,
            Library = null!,
            LibraryFolder = null!,
        };
        ReleaseGroup group = new()
        {
            Id = Guid.NewGuid(),
            Title = "Group",
            Cover = cover,
            AlbumReleaseGroup = [new() { AlbumId = album.Id, Album = album }],
        };
        _context.ReleaseGroups.Add(group);
        await _context.SaveChangesAsync();
        return (group, album);
    }

    private FanArtAlbum FanArtAlbumFor(Guid albumId, params string[] covers) =>
        new()
        {
            Name = "Group",
            Albums = new() { [albumId] = new() { Cover = [.. covers.Select(FanArtImage)] } },
        };

    [Fact]
    public async Task FanArtJob_Artist_FailedDownload_LeavesCoverUnchanged()
    {
        Artist artist = await SeedArtistAsync("/old.jpg");
        StubCoverFiles files = new(downloadable: []);
        FanArtImagesJob job = new() { CoverFiles = files };

        await job.StoreArtistImages(
            _context,
            new() { Thumbs = [FanArtImage("dead.jpg")] },
            artist.Id
        );

        (await _context.Artists.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
    }

    [Fact]
    public async Task FanArtJob_Artist_SuccessfulDownload_WritesFirstDownloadedFileName()
    {
        Artist artist = await SeedArtistAsync(null);
        StubCoverFiles files = new(downloadable: ["good.jpg"]);
        FanArtImagesJob job = new() { CoverFiles = files };

        await job.StoreArtistImages(
            _context,
            new() { Thumbs = [FanArtImage("dead.jpg"), FanArtImage("good.jpg")] },
            artist.Id
        );

        (await _context.Artists.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
        files.Attempted.Should().Equal("dead.jpg", "good.jpg");
    }

    [Fact]
    public async Task FanArtJob_Artist_CoverWithStoredFile_IsNotOverwritten()
    {
        Artist artist = await SeedArtistAsync("/kept.jpg");
        StubCoverFiles files = new(downloadable: ["new.jpg"], stored: ["kept.jpg"]);
        FanArtImagesJob job = new() { CoverFiles = files };

        await job.StoreArtistImages(
            _context,
            new() { Thumbs = [FanArtImage("new.jpg")] },
            artist.Id
        );

        (await _context.Artists.AsNoTracking().SingleAsync()).Cover.Should().Be("/kept.jpg");
        files.Attempted.Should().BeEmpty();
    }

    [Fact]
    public async Task FanArtJob_Release_FailedDownload_LeavesGroupAndAlbumCoverUnchanged()
    {
        (ReleaseGroup group, Album album) = await SeedReleaseAsync("/old.jpg");
        FanArtImagesJob job = new() { CoverFiles = new StubCoverFiles(downloadable: []) };

        await job.StoreReleaseImages(_context, FanArtAlbumFor(album.Id, "dead.jpg"), group.Id);

        (await _context.ReleaseGroups.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
    }

    [Fact]
    public async Task FanArtJob_Release_SuccessfulDownload_WritesFirstDownloadedFileName()
    {
        (ReleaseGroup group, Album album) = await SeedReleaseAsync(null);
        FanArtImagesJob job = new() { CoverFiles = new StubCoverFiles(downloadable: ["good.jpg"]) };

        await job.StoreReleaseImages(
            _context,
            FanArtAlbumFor(album.Id, "dead.jpg", "good.jpg"),
            group.Id
        );

        (await _context.ReleaseGroups.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
    }

    [Fact]
    public async Task FanArtManager_Release_FailedDownload_LeavesGroupAndAlbumCoverUnchanged()
    {
        (ReleaseGroup group, Album album) = await SeedReleaseAsync("/old.jpg");
        FanArtImageManager manager = new(new(_context), new StubCoverFiles(downloadable: []));

        await manager.StoreReleaseImages(FanArtAlbumFor(album.Id, "dead.jpg"), group.Id);

        (await _context.ReleaseGroups.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
    }

    [Fact]
    public async Task FanArtManager_Release_SuccessfulDownload_WritesFirstDownloadedFileName()
    {
        (ReleaseGroup group, Album album) = await SeedReleaseAsync(null);
        FanArtImageManager manager = new(
            new(_context),
            new StubCoverFiles(downloadable: ["good.jpg"])
        );

        await manager.StoreReleaseImages(
            FanArtAlbumFor(album.Id, "dead.jpg", "good.jpg"),
            group.Id
        );

        (await _context.ReleaseGroups.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
    }

    private async Task<Album> SeedAlbumWithTrackAsync(string? cover)
    {
        Album album = new()
        {
            Id = Guid.NewGuid(),
            Name = "Album",
            Cover = cover,
            Library = null!,
            LibraryFolder = null!,
        };
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = "Track",
            Cover = cover,
            LibraryFolder = null!,
            Metadata = null!,
        };
        album.AlbumTrack =
        [
            new()
            {
                AlbumId = album.Id,
                TrackId = track.Id,
                Track = track,
            },
        ];
        _context.Albums.Add(album);
        await _context.SaveChangesAsync();
        return album;
    }

    [Fact]
    public async Task Repair_CoverWithoutFile_QueuesRefetchForArtistAndAlbum()
    {
        Artist missingArtist = await SeedArtistAsync("/missing-artist.jpg");
        (ReleaseGroup group, Album missingAlbum) = await SeedReleaseAsync("/missing-album.jpg");
        MusicCoverRepairJob job = new() { CoverFiles = new StubCoverFiles(downloadable: []) };

        List<NoMercyQueue.Core.Interfaces.IShouldQueue> jobs = await job.FindRepairJobsAsync(
            _context
        );

        jobs.OfType<FanArtImagesJob>()
            .Select(j => (j.ArtistId, j.ReleaseGroupId))
            .Should()
            .BeEquivalentTo(new[] { (missingArtist.Id, Guid.Empty), (Guid.Empty, group.Id) });
        jobs.OfType<CoverArtImageJob>()
            .Should()
            .ContainSingle(j => j.ReleaseId == missingAlbum.Id && j.HasFrontCover);
    }

    [Fact]
    public async Task Repair_CoverWithStoredFileOrNoCover_QueuesNothing()
    {
        await SeedArtistAsync("/stored-artist.jpg");
        await SeedArtistAsync(null);
        await SeedArtistAsync("");
        await SeedReleaseAsync("/stored-album.jpg");
        MusicCoverRepairJob job = new()
        {
            CoverFiles = new StubCoverFiles(
                downloadable: [],
                stored: ["stored-artist.jpg", "stored-album.jpg"]
            ),
        };

        (await job.FindRepairJobsAsync(_context)).Should().BeEmpty();
    }

    [Fact]
    public async Task CoverArtJob_FailedDownload_LeavesAlbumAndTrackCoverUnchanged()
    {
        Album album = await SeedAlbumWithTrackAsync("/old.jpg");
        CoverArtImageJob job = new()
        {
            ReleaseId = album.Id,
            CoverFiles = new StubCoverFiles(downloadable: []),
        };

        await job.StoreCovers(_context, new() { Images = [FrontCover("dead.jpg")] });

        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
        (await _context.Tracks.AsNoTracking().SingleAsync()).Cover.Should().Be("/old.jpg");
    }

    [Fact]
    public async Task CoverArtJob_SuccessfulDownload_WritesFirstDownloadedFileName()
    {
        Album album = await SeedAlbumWithTrackAsync(null);
        CoverArtImageJob job = new()
        {
            ReleaseId = album.Id,
            CoverFiles = new StubCoverFiles(downloadable: ["good.jpg"]),
        };

        await job.StoreCovers(
            _context,
            new() { Images = [FrontCover("dead.jpg"), FrontCover("good.jpg")] }
        );

        (await _context.Albums.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
        (await _context.Tracks.AsNoTracking().SingleAsync()).Cover.Should().Be("/good.jpg");
    }
}
