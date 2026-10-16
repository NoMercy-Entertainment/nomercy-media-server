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
using NoMercy.NmSystem.Dto;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// A file name with an episode number too big for an int, such as a date and
/// time after the E, must not cost the other files in its folder their place
/// in the scan (#243).
/// </summary>
[Trait("Category", "Unit")]
public sealed class MediaScanEpisodeOverflowTests : IDisposable
{
    private const string OverflowName = "A.Show.S01E20240101123000.mkv";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_scan_overflow_" + Guid.NewGuid().ToString("N")
    );

    // More normal files than the scan works on at once, so the ones still
    // waiting when the bad name threw were never looked at.
    private readonly int _normalCount = Math.Max(16, Environment.ProcessorCount * 2);

    public MediaScanEpisodeOverflowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, OverflowName), []);
        for (int episode = 1; episode <= _normalCount; episode++)
            File.WriteAllBytes(Path.Combine(_root, $"B.Show.S01E{episode:D2}.mkv"), []);
    }

    [Fact]
    public async Task A_file_with_an_oversized_episode_number_leaves_the_folder_scan_whole()
    {
        MediaFile[] files = await ScanAsync();

        files
            .Count(file => file.Name.StartsWith("B.Show."))
            .Should()
            .Be(_normalCount, "one bad name must not end the scan of its folder");
        files.Should().ContainSingle(file => file.Name == OverflowName);

        MediaFile third = files.Single(file => file.Name == "B.Show.S01E03.mkv");
        third.Parsed!.Season.Should().Be(1);
        third.Parsed.Episode.Should().Be(3);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<MediaFile[]> ScanAsync()
    {
        await using MediaScan mediaScan = new(new LocalStorageDriver());
        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .FilterByMediaType("video")
            .Process(_root, 0);

        return [.. folders.SelectMany(folder => folder.Files ?? [])];
    }
}
