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

using System.Collections.Concurrent;
using Moq;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Dto;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// Reproduces the production scan regression where a freshly-encoded HLS show
/// (episode folders each holding a <c>*.NoMercy.m3u8</c> master) is enumerated as
/// zero files. Drives the real <see cref="MediaScan"/> exactly as
/// <c>FileManager.GetFiles</c> does for an anime library (file listing on, regex
/// filter off, video extension filter, depth 2), over a temp tree.
/// </summary>
[Trait("Category", "Unit")]
public class MediaScanHlsDiscoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_scan_" + Guid.NewGuid().ToString("N")
    );

    // The exact production folder name for Hensuki (id 88061): embeds a comma and
    // an apostrophe. The control is a clean name (One Piece style) that is known
    // to scan correctly in production.
    private const string HensukiName =
        "Hensuki.Are.You.Willing.to.Fall.in.Love.With.a.Pervert,.As.Long.As.She's.a.Cutie.(2019)";
    private const string CleanName = "One.Piece.(1999)";

    [Fact]
    public async Task Process_does_not_stat_excluded_subtitle_segments_as_video_candidates()
    {
        string[] segments = [.. Enumerable.Range(0, 1000).Select(i => $"root/subtitle_{i}.vtt")];
        Mock<IStorageDriver> driver = new();
        driver.Setup(d => d.GetFullPath(It.IsAny<string>())).Returns((string path) => path);
        driver.Setup(d => d.DirectoryExists("root")).Returns(true);
        driver
            .Setup(d => d.EnumerateFileSystemEntries("root", "*", SearchOption.TopDirectoryOnly))
            .Returns(segments);

        await using MediaScan scan = new(driver.Object);
        await scan.EnableFileListing().FilterByMediaType("tv").Process("root", 0);

        driver.Verify(d => d.FileExists(It.IsAny<string>()), Times.Never);
    }

    [Theory]
    [InlineData(
        HensukiName,
        "Hensuki.Are.You.Willing.to.Fall.in.Love.With.a.Pervert,.As.Long.As.She's.a.Cutie"
    )]
    [InlineData(CleanName, "One.Piece")]
    public async Task Process_FindsHlsMasters_ForEachEpisodeFolder(
        string showFolderName,
        string episodeBase
    )
    {
        string showFolder = Path.Combine(_root, showFolderName);
        Directory.CreateDirectory(showFolder);

        for (int ep = 1; ep <= 3; ep++)
        {
            string episodeFolder = Path.Combine(showFolder, $"{episodeBase}.S01E{ep:D2}");
            Directory.CreateDirectory(episodeFolder);

            File.WriteAllText(
                Path.Combine(
                    episodeFolder,
                    $"{episodeBase}.S01E{ep:D2}.Episode.Title.NoMercy.m3u8"
                ),
                "#EXTM3U\n#EXT-X-VERSION:6\n"
            );
            File.WriteAllText(Path.Combine(episodeFolder, "chapters.vtt"), "WEBVTT\n");
            File.WriteAllText(Path.Combine(episodeFolder, "fonts.json"), "[]");
            File.WriteAllText(Path.Combine(episodeFolder, "thumbs_160x90.vtt"), "WEBVTT\n");
            foreach (
                string sub in new[]
                {
                    "video_1920x1080_SDR",
                    "audio_eng_aac",
                    "audio_jpn_aac",
                    "subtitles",
                    "fonts",
                }
            )
                Directory.CreateDirectory(Path.Combine(episodeFolder, sub));
        }

        MediaScan mediaScan = new(new LocalStorageDriver());
        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .FilterByMediaType("anime")
            .FilterByFileName(null)
            .Process(showFolder, 2);
        await mediaScan.DisposeAsync();

        int rawFileCount = folders.Sum(folder => folder.Files?.Count ?? 0);
        bool hasCandidates = folders
            .SelectMany(folder => folder.Files ?? [])
            .Any(file => file.Parsed is not null);

        rawFileCount.Should().Be(3, "each of the 3 episode folders holds one .NoMercy.m3u8 master");
        hasCandidates.Should().BeTrue("the discovered m3u8 masters must be parseable candidates");
    }

    [Fact]
    public async Task An_inbox_scan_lists_video_and_audio_files()
    {
        Directory.CreateDirectory(_root);
        // Empty files: the probe fails with or without the ffprobe binary.
        foreach (
            string name in new[]
            {
                "clip.mkv",
                "clip.webm",
                "song.flac",
                "song.opus",
                "notes.txt",
                "subtitle.srt",
            }
        )
            File.WriteAllBytes(Path.Combine(_root, name), []);

        await using MediaScan mediaScan = new(new LocalStorageDriver());
        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .FilterByMediaType(MediaTypes.InboxMediaType)
            .Process(_root, 0);

        folders
            .SelectMany(folder => folder.Files ?? [])
            .Select(file => file.Name)
            .Should()
            .BeEquivalentTo("clip.mkv", "clip.webm", "song.flac", "song.opus");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
        GC.SuppressFinalize(this);
    }
}
