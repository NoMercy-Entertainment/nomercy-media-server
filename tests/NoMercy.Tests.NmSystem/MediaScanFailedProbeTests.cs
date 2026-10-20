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
/// A file whose probe failed is still listed, but it carries no probe: every
/// consumer of <see cref="MediaFile.FFprobe" /> already reads null as "not
/// probed", while a failed probe used to pass as a file with no streams (#320).
/// </summary>
[Trait("Category", "Unit")]
public sealed class MediaScanFailedProbeTests : IDisposable
{
    private const string FileName = "A.Movie.2020.mkv";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nm_scan_failed_probe_" + Guid.NewGuid().ToString("N")
    );

    public MediaScanFailedProbeTests()
    {
        Directory.CreateDirectory(_root);
        // Empty: ffprobe cannot open it, so the probe fails whether the binary
        // is present or not.
        File.WriteAllBytes(Path.Combine(_root, FileName), []);
    }

    [Fact]
    public async Task A_file_whose_probe_failed_is_listed_without_a_probe()
    {
        await using MediaScan mediaScan = new(new LocalStorageDriver());
        ConcurrentBag<MediaFolderExtend> folders = await mediaScan
            .EnableFileListing()
            .DisableRegexFilter()
            .FilterByMediaType("video")
            .Process(_root, 0);

        MediaFile[] files = [.. folders.SelectMany(folder => folder.Files ?? [])];

        MediaFile file = files.Should().ContainSingle(f => f.Name == FileName).Subject;
        file.FFprobe.Should().BeNull("a probe that failed must not look like a probe");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
