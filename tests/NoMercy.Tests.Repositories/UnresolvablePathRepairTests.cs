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
using NoMercy.Data.Services;
using NoMercy.Database;
using NoMercy.Database.Models.Music;

namespace NoMercy.Tests.Repositories;

// The rows this sweep removes are the ones the scanner used to write before it
// validated its paths: a Folder that is neither empty nor root-relative (an
// unstripped host path such as "M:/Download/complete/..."), or a Filename that
// names no file. Both compose into /{FolderId}{Folder}{Filename} URLs that no
// client can resolve, so they are unplayable entries the UI still lists.
[Trait("Category", "Unit")]
public class UnresolvablePathRepairTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public UnresolvablePathRepairTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        // Foreign keys stay ON. Every child of Tracks — AlbumTrack, ArtistTrack,
        // LibraryTrack, PlaylistTrack, MusicGenreTrack, MusicPlays, TrackUser,
        // Images — is ON DELETE RESTRICT, so a sweep that deletes the track
        // alone throws against a real database. Switching the constraint off
        // here would hide exactly the failure these tests exist to catch.
        using (SqliteCommand foreignKeysOn = _connection.CreateCommand())
        {
            foreignKeysOn.CommandText = "PRAGMA foreign_keys = ON;";
            foreignKeysOn.ExecuteNonQuery();
        }

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

    private static readonly Ulid DriverId = Ulid.NewUlid();
    private static readonly Ulid FolderId = Ulid.NewUlid();

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private UnresolvablePathRepair BuildRepair() =>
        new(new TestDbContextFactory(_options), NullLogger<UnresolvablePathRepair>.Instance);

    private static Track Track(string name, string? folder, string? filename) =>
        new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            Folder = folder,
            Filename = filename,
            FolderId = FolderId,
            Duration = "03:12",
        };

    [Fact]
    public async Task Removes_a_track_whose_folder_kept_the_unstripped_host_path()
    {
        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(
                Track(
                    "Where The Streets Have No Name",
                    "M:/Download/complete/U2",
                    "/01. Where.flac"
                )
            );
            await ctx.SaveChangesAsync();
        }

        int removed = await BuildRepair().RunAsync(CancellationToken.None);

        removed.Should().Be(1);
        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task Removes_a_track_whose_filename_names_no_file()
    {
        // The reported record: Folder and Filename both collapsed, so the
        // composed URL was /{FolderId}/ and addressed nothing.
        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(Track("Nameless", string.Empty, "/"));
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(1);

        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task Keeps_a_track_that_sits_directly_in_the_library_root()
    {
        // An empty folder is legitimate: the file lives in the root itself, so
        // /{FolderId}/loose.flac resolves.
        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(Track("Loose", string.Empty, "/loose.flac"));
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(0);

        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().HaveCount(1);
    }

    [Fact]
    public async Task Keeps_a_track_with_a_root_relative_folder()
    {
        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(Track("Bad", "/U2/The Joshua Tree", "/01. Where.flac"));
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(0);

        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().HaveCount(1);
    }

    [Fact]
    public async Task Removes_a_track_that_is_still_linked_to_its_album_artist_and_library()
    {
        // Every child of Tracks is ON DELETE RESTRICT, and a scanned track is
        // never childless — it was linked to its album, artist and library the
        // moment it was stored. Deleting the track row alone fails on the live
        // database, which is what a real boot showed.
        Ulid libraryId = Ulid.NewUlid();
        Guid genreId = Guid.NewGuid();
        Track track = Track("Linked", "M:/Download/complete/U2", "/01. Where.flac");

        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(track);
            ctx.Libraries.Add(
                new()
                {
                    Id = libraryId,
                    Title = "Music",
                    Type = "music",
                }
            );
            ctx.MusicGenres.Add(new() { Id = genreId, Name = "rock" });
            await ctx.SaveChangesAsync();

            ctx.LibraryTrack.Add(new(libraryId, track.Id));
            ctx.MusicGenreTrack.Add(new() { GenreId = genreId, TrackId = track.Id });
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(1);

        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().BeEmpty();
        assertCtx.LibraryTrack.Should().BeEmpty();
        assertCtx.MusicGenreTrack.Should().BeEmpty();
        // The library and the genre survive: only the unplayable file leaves.
        assertCtx.Libraries.Should().HaveCount(1);
        assertCtx.MusicGenres.Should().HaveCount(1);
    }

    [Fact]
    public async Task Removes_a_video_file_whose_folder_kept_the_nas_host_prefix()
    {
        await using (MediaContext ctx = new(_options))
        {
            ctx.VideoFiles.Add(
                new()
                {
                    Folder = "192.168.2.120/mnt/vault/Media/Libraries/Anime/Steins.Gate.(2011)",
                    HostFolder = "192.168.2.120/mnt/vault/Media/Libraries/Anime/Steins.Gate.(2011)",
                    Filename = "/Steins;Gate.S02E01.NoMercy.m3u8",
                    Share = Ulid.NewUlid().ToString(),
                    Duration = "24:00",
                    Chapters = "",
                    Languages = "[]",
                    Quality = "1920",
                    Subtitles = "[]",
                }
            );
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(1);

        await using MediaContext assertCtx = new(_options);
        assertCtx.VideoFiles.Should().BeEmpty();
    }

    [Fact]
    public async Task Leaves_a_healthy_library_untouched_and_reports_nothing_removed()
    {
        await using (MediaContext ctx = new(_options))
        {
            ctx.Tracks.Add(Track("Good", "/U2/The Joshua Tree", "/01. Where.flac"));
            ctx.VideoFiles.Add(
                new()
                {
                    Folder = "/Steins.Gate.(2011)/Season 1",
                    HostFolder = "/mnt/vault/Media/Libraries/Anime/Steins.Gate.(2011)/Season 1",
                    Filename = "/Steins;Gate.S01E01.NoMercy.m3u8",
                    Share = Ulid.NewUlid().ToString(),
                    Duration = "24:00",
                    Chapters = "",
                    Languages = "[]",
                    Quality = "1920",
                    Subtitles = "[]",
                }
            );
            await ctx.SaveChangesAsync();
        }

        (await BuildRepair().RunAsync(CancellationToken.None)).Should().Be(0);

        await using MediaContext assertCtx = new(_options);
        assertCtx.Tracks.Should().HaveCount(1);
        assertCtx.VideoFiles.Should().HaveCount(1);
    }

    private sealed class TestDbContextFactory(DbContextOptions<MediaContext> options)
        : IDbContextFactory<MediaContext>
    {
        public MediaContext CreateDbContext() => new(options);
    }
}
