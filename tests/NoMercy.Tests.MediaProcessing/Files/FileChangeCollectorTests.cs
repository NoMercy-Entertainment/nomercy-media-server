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

using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem.Domain;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan span) => _now += span;
}

/// <summary>
/// The watcher acts on a folder only when its files are finished and the change is not the
/// server's own: no import while a file is being written, none for the server's output.
/// </summary>
[Trait("Category", "Unit")]
public sealed class FileChangeCollectorTests
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);
    private const string Folder = "/mnt/media/Movies/Synthetic Movie (2020)";

    private readonly ManualClock _clock = new();
    private readonly ServerWriteLedger _ledger;
    private readonly List<FileChangeGroup> _published = [];
    private readonly Dictionary<string, FileSignature?> _disk = new();
    private readonly FileChangeCollector _collector;
    private readonly Library _movies = new() { Type = MediaTypes.MovieMediaType };

    public FileChangeCollectorTests()
    {
        _ledger = new(_clock);
        _collector = new(
            group =>
            {
                _published.Add(group);
                return Task.CompletedTask;
            },
            _ledger,
            _clock,
            Quiet,
            path => _disk.GetValueOrDefault(path)
        );
    }

    private void Write(string path, long length) =>
        _disk[path] = new FileSignature(length, _clock.GetUtcNow().UtcDateTime);

    [Theory]
    [InlineData("/Synthetic Movie.mkv.part")]
    [InlineData("/Synthetic Movie.mkv.partial")]
    [InlineData("/Synthetic Movie.mkv.tmp")]
    [InlineData("/Synthetic Movie.mkv.crdownload")]
    [InlineData("/Synthetic Movie.mkv.12ab.nmpart")]
    [InlineData("/~Synthetic Movie.mkv")]
    [InlineData("/._Synthetic Movie.mkv")]
    public void A_temp_file_is_not_accepted(string name)
    {
        _collector.Accepts(Folder + name).Should().BeFalse();
    }

    [Fact]
    public void A_finished_media_file_is_accepted()
    {
        _collector.Accepts(Folder + "/Synthetic Movie.mkv").Should().BeTrue();
    }

    [Fact]
    public async Task A_growing_file_is_not_imported_until_it_has_been_stable_for_a_quiet_period()
    {
        string file = Folder + "/Synthetic Movie.mkv";
        Write(file, 100);
        _collector.Observe(_movies, WatcherChangeTypes.Created, file);

        _clock.Advance(Quiet);
        Write(file, 5000);
        await _collector.TickAsync();
        _published.Should().BeEmpty("the file grew during the quiet period");

        _clock.Advance(Quiet);
        await _collector.TickAsync();
        _published.Should().ContainSingle();
    }

    [Fact]
    public async Task A_temp_file_renamed_into_place_is_imported_as_new_content()
    {
        string file = Folder + "/Synthetic Movie.mkv";
        Write(file, 5000);
        _collector.Observe(_movies, WatcherChangeTypes.Renamed, file, file + ".part");

        _clock.Advance(Quiet);
        await _collector.TickAsync();

        _published
            .Should()
            .ContainSingle()
            .Which.Changes.Should()
            .ContainSingle()
            .Which.ChangeType.Should()
            .Be(WatcherChangeTypes.Created);
    }

    [Fact]
    public void Output_the_encoder_publishes_into_a_held_folder_is_not_accepted_even_outside_the_output_folder_names()
    {
        string outputFolder = Folder + "/Synthetic Movie.out";
        using IDisposable hold = _ledger.Hold(outputFolder);

        _collector.Accepts(outputFolder + "/Synthetic Movie.m3u8").Should().BeFalse();
        _collector.Accepts(outputFolder + "/Synthetic Movie.mp4").Should().BeFalse();
        _collector.Accepts(Folder + "/Other Movie.mkv").Should().BeTrue();
    }

    [Fact]
    public void A_held_folder_stays_ignored_while_its_last_events_arrive_and_then_counts_again()
    {
        string outputFolder = Folder + "/Synthetic Movie.out";
        _ledger.Hold(outputFolder).Dispose();

        _collector.Accepts(outputFolder + "/Synthetic Movie.m3u8").Should().BeFalse();

        _clock.Advance(ServerWriteLedger.Linger + TimeSpan.FromSeconds(1));
        _collector.Accepts(outputFolder + "/Synthetic Movie.m3u8").Should().BeTrue();
    }

    [Fact]
    public void A_move_the_server_made_is_not_accepted_at_either_end_but_another_servers_move_is()
    {
        string from = Folder + "/Old Name.mkv";
        string to = Folder + "/New Name.mkv";
        _ledger.Note(from);
        _ledger.Note(to);

        _collector.Accepts(to, from).Should().BeFalse();
        _collector.Accepts(Folder + "/Other New.mkv", Folder + "/Other Old.mkv").Should().BeTrue();
    }

    [Fact]
    public void A_path_under_the_transcode_root_is_not_accepted()
    {
        FileChangeCollector collector = new(
            _ => Task.CompletedTask,
            _ledger,
            _clock,
            Quiet,
            transcodeRoot: () => "/var/cache/synthetic/encoder"
        );

        collector.Accepts("/var/cache/synthetic/encoder/Show/seg_00001.mp4").Should().BeFalse();
    }

    [Theory]
    [InlineData(MediaTypes.MovieMediaType)]
    [InlineData(MediaTypes.TvMediaType)]
    [InlineData(MediaTypes.AnimeMediaType)]
    [InlineData(MediaTypes.MusicMediaType)]
    [InlineData(MediaTypes.InboxMediaType)]
    public void The_side_files_an_import_writes_never_pass_the_extension_filter(string libraryType)
    {
        foreach (
            string name in new[]
            {
                "movie.nfo",
                "poster.jpg",
                "backdrop.webp",
                "metadata.json",
                "lyrics.lrc",
                "subtitle.srt",
                "subtitle.vtt",
                "notes.txt",
                "movie.xml",
            }
        )
            FileChangeCollector
                .IsAllowedExtension(libraryType, Folder + "/" + name)
                .Should()
                .BeFalse(name);
    }
}
