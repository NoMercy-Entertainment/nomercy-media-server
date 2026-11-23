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
using NoMercy.Data.Services;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// The folder the server kept derived audio in is removed after the update
/// that took music analysis out (#689). Run against a real folder: what is
/// under test is what ends up on disk.
/// </summary>
[Trait("Category", "Integration")]
public class RetiredDerivedAudioFolderTests : IDisposable
{
    private readonly string _cacheFolder = Path.Combine(
        Path.GetTempPath(),
        $"nm_retired_derived_audio_{Guid.NewGuid():N}"
    );

    private readonly RetiredDerivedAudioFolder _folder;

    public RetiredDerivedAudioFolderTests()
    {
        Directory.CreateDirectory(_cacheFolder);

        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([_cacheFolder], driver);
        _folder = new(new LocalStorage(driver, guard));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cacheFolder))
            Directory.Delete(_cacheFolder, recursive: true);

        GC.SuppressFinalize(this);
    }

    private string Seed(string relativePath, int bytes)
    {
        string path = Path.Combine(_cacheFolder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);

        return path;
    }

    [Fact]
    public async Task TheFolder_IsRemovedWithAllItHolds_AndItsSizeIsGivenBack()
    {
        Seed("derived/ab/abcdef.opus", 700);
        Seed("derived/cd/cdefab.opus", 300);
        Seed("derived/.tmp/half-written", 24);

        long? freed = await _folder.RemoveAsync(CancellationToken.None);

        freed.Should().Be(1024);
        Directory.Exists(Path.Combine(_cacheFolder, "derived")).Should().BeFalse();
    }

    [Fact]
    public async Task TheRestOfTheCache_IsLeftAlone()
    {
        Seed("derived/ab/abcdef.opus", 10);
        string transcode = Seed("transcode/a-trailer/video.m3u8", 10);
        string image = Seed("images/music/cover.jpg", 10);

        await _folder.RemoveAsync(CancellationToken.None);

        File.Exists(transcode).Should().BeTrue();
        File.Exists(image).Should().BeTrue();
    }

    [Fact]
    public async Task AnEmptyFolder_IsRemoved_AndGivesNothingBack()
    {
        Directory.CreateDirectory(Path.Combine(_cacheFolder, "derived"));

        long? freed = await _folder.RemoveAsync(CancellationToken.None);

        freed.Should().Be(0);
        Directory.Exists(Path.Combine(_cacheFolder, "derived")).Should().BeFalse();
    }

    [Fact]
    public async Task WithNoFolder_ThereIsNothingToDo()
    {
        long? freed = await _folder.RemoveAsync(CancellationToken.None);

        freed.Should().BeNull();
    }

    [Fact]
    public async Task ASecondStart_FindsNothingLeft()
    {
        Seed("derived/ab/abcdef.opus", 10);

        await _folder.RemoveAsync(CancellationToken.None);
        long? second = await _folder.RemoveAsync(CancellationToken.None);

        second.Should().BeNull();
    }
}
