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
using Moq;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using NoMercy.MediaProcessing.Files;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// A widened sprite sheet has to be the one clients are told about.
///
/// A scan registers the pair it finds and queues the upgrade in the same pass.
/// The upgrade then writes <c>thumbs_320x180</c> and deletes the 160-wide pair it
/// superseded, which left the registration naming a file the server had just
/// removed: every scrub on an upgraded title answered 404 until some later scan
/// happened to re-read the folder.
/// </summary>
public class RepointPreviewTracksTests : IDisposable
{
    private const string HostFolder = "/mnt/library/Anime/Show/Show.S01E01";

    /// The repository never reaches the driver on this path; it is a constructor
    /// argument and nothing more.
    private static IStorageDriver Driver => new Mock<IStorageDriver>(MockBehavior.Loose).Object;

    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;

    public RepointPreviewTracksTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();

        using (SqliteCommand foreignKeys = _connection.CreateCommand())
        {
            foreignKeys.CommandText = "PRAGMA foreign_keys = OFF;";
            foreignKeys.ExecuteNonQuery();
        }

        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;
        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
    }

    private async Task StoreVideoFileAsync(string hostFolder, VideoTrack[] tracks)
    {
        await using MediaContext context = new(_options);
        context.VideoFiles.Add(
            new()
            {
                Filename = "/Show.S01E01.mkv",
                Folder = "/Show/Show.S01E01",
                HostFolder = hostFolder,
                Share = "share",
                Quality = "1080",
                Languages = "[]",
                Tracks = tracks,
            }
        );
        await context.SaveChangesAsync();
    }

    private static VideoTrack[] TheSheetTheScanFound() =>
        [
            new() { File = "/thumbs_160x90.vtt", Kind = "thumbnails" },
            new() { File = "/thumbs_160x90.webp", Kind = "sprite" },
            new() { File = "/chapters.vtt", Kind = "chapters" },
        ];

    private async Task<VideoTrack[]> TracksAsStoredAsync()
    {
        await using MediaContext context = new(_options);
        VideoFile videoFile = await context.VideoFiles.AsNoTracking().FirstAsync();
        return videoFile.Tracks;
    }

    [Fact]
    public async Task Repoints_both_halves_of_the_pair_at_the_widened_sheet()
    {
        await StoreVideoFileAsync(HostFolder, TheSheetTheScanFound());

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        int repointed = await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        repointed.Should().Be(1);

        VideoTrack[] tracks = await TracksAsStoredAsync();
        tracks.Single(track => track.Kind == "thumbnails").File.Should().Be("/thumbs_320x180.vtt");
        tracks.Single(track => track.Kind == "sprite").File.Should().Be("/thumbs_320x180.webp");
    }

    [Fact]
    public async Task Repoints_the_preview_metadata_the_watch_response_reads()
    {
        // The watch response trusts the scanned preview metadata over the track
        // rows. Repointing only the rows left it naming the sheet the rebuild had
        // just deleted, so players fetched sprite.webp and previews.vtt and got 404.
        Metadata metadata = new()
        {
            Filename = "/Show.S01E01.mkv",
            Folder = "/Show/Show.S01E01",
            HostFolder = HostFolder,
            Previews =
            [
                new()
                {
                    ImageFileName = "/sprite.webp",
                    ImageFileSize = 365456,
                    TimeFileName = "/previews.vtt",
                    TimeFileSize = 23385,
                },
            ],
        };
        await using (MediaContext seed = new(_options))
        {
            seed.Metadata.Add(metadata);
            seed.VideoFiles.Add(
                new()
                {
                    Filename = "/Show.S01E01.mkv",
                    Folder = "/Show/Show.S01E01",
                    HostFolder = HostFolder,
                    Share = "share",
                    Quality = "1080",
                    Languages = "[]",
                    MetadataId = metadata.Id,
                    Tracks =
                    [
                        new() { File = "/sprite.webp", Kind = "sprite" },
                        new() { File = "/previews.vtt", Kind = "thumbnails" },
                    ],
                }
            );
            await seed.SaveChangesAsync();
        }

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        await using MediaContext read = new(_options);
        IPreview preview = (await read.Metadata.AsNoTracking().SingleAsync()).Previews!.Single();
        preview.ImageFileName.Should().Be("/thumbs_320x180.webp");
        preview.TimeFileName.Should().Be("/thumbs_320x180.vtt");
    }

    [Fact]
    public async Task Registers_the_rebuilt_cue_file_a_legacy_folder_never_had()
    {
        // What titles encoded before the tile size went into the name still look
        // like: a `sprite` sheet and no cue file at all. Repointing the sheet on
        // its own left nothing naming the VTT the rebuild had just written, and
        // both clients read the preview from the `thumbnails` entry alone — so
        // the scrub stayed blank until a full scan happened to re-read the folder.
        await StoreVideoFileAsync(
            HostFolder,
            [
                new() { File = "/sprite.webp", Kind = "sprite" },
                new() { File = "/chapters.vtt", Kind = "chapters" },
            ]
        );

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        int repointed = await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        repointed.Should().Be(1);

        VideoTrack[] tracks = await TracksAsStoredAsync();
        tracks.Single(track => track.Kind == "thumbnails").File.Should().Be("/thumbs_320x180.vtt");
        tracks.Single(track => track.Kind == "sprite").File.Should().Be("/thumbs_320x180.webp");
    }

    [Fact]
    public async Task Leaves_every_other_track_alone()
    {
        await StoreVideoFileAsync(HostFolder, TheSheetTheScanFound());

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        VideoTrack[] tracks = await TracksAsStoredAsync();
        tracks.Single(track => track.Kind == "chapters").File.Should().Be("/chapters.vtt");
    }

    [Fact]
    public async Task Touches_nothing_in_a_folder_it_was_not_asked_about()
    {
        // The path a job carries is driver-shaped; the column is normalised. A
        // match that is too loose would repoint a neighbouring title at a sheet
        // that does not exist in its folder.
        await StoreVideoFileAsync("/mnt/library/Anime/Show/Show.S01E02", TheSheetTheScanFound());

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        int repointed = await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        repointed.Should().Be(0);
        (await TracksAsStoredAsync())
            .Single(track => track.Kind == "thumbnails")
            .File.Should()
            .Be("/thumbs_160x90.vtt");
    }

    [Fact]
    public async Task Matches_a_folder_the_job_carries_with_backslashes()
    {
        await StoreVideoFileAsync("C:/Media/Anime/Show/Show.S01E01", TheSheetTheScanFound());

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        int repointed = await repository.RepointPreviewTracksAsync(
            @"C:\Media\Anime\Show\Show.S01E01",
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        repointed.Should().Be(1);
    }

    [Fact]
    public async Task Reports_nothing_repointed_when_the_pair_is_already_current()
    {
        // The job is re-queued across scans until it lands, so a second pass over
        // a folder that is already right has to be a no-op, not a write.
        await StoreVideoFileAsync(
            HostFolder,
            [
                new() { File = "/thumbs_320x180.vtt", Kind = "thumbnails" },
                new() { File = "/thumbs_320x180.webp", Kind = "sprite" },
            ]
        );

        await using MediaContext context = new(_options);
        FileRepository repository = new(context, Driver);

        int repointed = await repository.RepointPreviewTracksAsync(
            HostFolder,
            "thumbs_320x180.webp",
            "thumbs_320x180.vtt"
        );

        repointed.Should().Be(0);
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
