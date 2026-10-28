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

/// <summary>
/// An encode writing hundreds of files and an upload growing over several events queue no
/// import until the upload is stable, and then exactly one.
/// </summary>
[Trait("Category", "Unit")]
public sealed class WatcherLoopSimulationTests : IDisposable
{
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(10);

    private readonly string _root;
    private readonly ManualClock _clock = new();
    private readonly ServerWriteLedger _ledger;
    private readonly List<FileChangeGroup> _published = [];
    private readonly FileChangeCollector _collector;
    private readonly Library _library = new() { Type = MediaTypes.TvMediaType };

    public WatcherLoopSimulationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"nm-watch-sim-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _ledger = new(_clock);
        _collector = new(
            group =>
            {
                _published.Add(group);
                return Task.CompletedTask;
            },
            _ledger,
            _clock,
            Quiet
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private string InRoot(params string[] parts) => Path.Combine([_root, .. parts]);

    private void Raise(WatcherChangeTypes type, string path, string? old = null)
    {
        _collector.Handle(_library, type, path, old, Directory.Exists);
        _collector.Handle(_library, WatcherChangeTypes.Changed, path, null, Directory.Exists);
    }

    private void SimulateEncodePublish(string outputFolder, int segments)
    {
        using IDisposable hold = _ledger.Hold(outputFolder);
        Directory.CreateDirectory(outputFolder);
        for (int index = 0; index < segments; index++)
        {
            string segment = Path.Combine(outputFolder, $"segment_{index:00000}.mp4");
            File.WriteAllText(segment, "segment");
            Raise(WatcherChangeTypes.Created, segment);
        }

        string playlist = Path.Combine(outputFolder, "playlist.m3u8");
        File.WriteAllText(playlist, "#EXTM3U");
        Raise(WatcherChangeTypes.Created, playlist);
    }

    [Fact]
    public async Task An_encode_of_two_hundred_segments_queues_no_import()
    {
        string episode = InRoot("Synthetic Show", "Synthetic Show.S01E01");
        SimulateEncodePublish(Path.Combine(episode, "rendition"), 200);
        string master = Path.Combine(episode, "Synthetic Show.S01E01.m3u8");
        using (_ledger.Hold(episode))
        {
            File.WriteAllText(master, "#EXTM3U");
            Raise(WatcherChangeTypes.Created, master);
        }

        _clock.Advance(Quiet * 3);
        await _collector.TickAsync();

        _published.Should().BeEmpty();
    }

    [Fact]
    public async Task An_upload_growing_over_several_events_queues_one_import_once_it_is_stable()
    {
        string movie = InRoot("Synthetic Movie (2020)", "Synthetic Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(movie)!);
        File.WriteAllText(movie, "start");
        Raise(WatcherChangeTypes.Created, movie);

        for (int step = 0; step < 5; step++)
        {
            File.AppendAllText(movie, new string('x', 1000));
            _clock.Advance(TimeSpan.FromSeconds(4));
            await _collector.TickAsync();
            _published.Should().BeEmpty($"the upload was still growing at step {step}");
        }

        _clock.Advance(Quiet);
        await _collector.TickAsync();
        _published.Should().BeEmpty("the file changed since the last look, so the wait restarts");

        _clock.Advance(Quiet);
        await _collector.TickAsync();

        _published.Should().ContainSingle();
    }

    [Fact]
    public async Task An_encode_and_an_upload_together_queue_exactly_one_import()
    {
        string movie = InRoot("Synthetic Movie (2020)", "Synthetic Movie.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(movie)!);
        File.WriteAllText(movie, "start");
        Raise(WatcherChangeTypes.Created, movie);
        SimulateEncodePublish(InRoot("Synthetic Show", "Synthetic Show.S01E01", "rendition"), 200);

        _clock.Advance(Quiet * 3);
        await _collector.TickAsync();

        _published
            .Should()
            .ContainSingle()
            .Which.FolderPath.Should()
            .EndWith("Synthetic Movie (2020)");
    }
}
