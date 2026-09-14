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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.MediaProcessing.Trailers;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.MediaProcessing.Trailers;

[Trait("Category", "Unit")]
public sealed class TrailerCacheTests : IDisposable
{
    private const string TrailerId = "dQw4w9WgXcQ";

    private readonly string _transcodeRoot = Path.Combine(
        Path.GetTempPath(),
        $"nm-trailercache-{Guid.NewGuid():N}"
    );

    private readonly TrailerCache _cache;

    public TrailerCacheTests()
    {
        Directory.CreateDirectory(_transcodeRoot);
        LocalStorageDriver driver = new();
        _cache = new(
            new LocalStorage(driver, new StoragePathGuard([_transcodeRoot], driver)),
            NullLogger<TrailerCache>.Instance
        );
    }

    public void Dispose()
    {
        if (Directory.Exists(_transcodeRoot))
            Directory.Delete(_transcodeRoot, recursive: true);
    }

    private void StoreInfo(string json)
    {
        Directory.CreateDirectory(Path.Combine(_transcodeRoot, TrailerId));
        File.WriteAllText(Path.Combine(_transcodeRoot, TrailerId, "info.json"), json);
    }

    [Theory]
    [InlineData("dQw4w9WgXcQ", true)]
    [InlineData("a-b_c1234XY", true)]
    [InlineData("short", false)]
    [InlineData("dQw4w9WgXcQ1", false)]
    [InlineData("../../etc/pa", false)]
    [InlineData("abc\"; rm -rf", false)]
    [InlineData("dQw4w9WgXc&", false)]
    public void IsValidId_AcceptsOnlyAYouTubeId(string trailerId, bool expected)
    {
        TrailerCache.IsValidId(trailerId).Should().Be(expected);
    }

    [Fact]
    public async Task FetchInfoAsync_StoredInfo_IsFoundWithoutFetching()
    {
        StoreInfo("""{"id":"dQw4w9WgXcQ","title":"Trailer","duration":90}""");

        (await _cache.FetchInfoAsync(TrailerId)).Should().BeTrue();
    }

    [Fact]
    public async Task ReadInfoAsync_ParsesTheStoredInfo()
    {
        StoreInfo(
            """{"id":"dQw4w9WgXcQ","title":"Trailer","duration":90,"formats":[{"protocol":"https"}]}"""
        );

        TrailerInfo? info = await _cache.ReadInfoAsync(TrailerId);

        info.Should().NotBeNull();
        info!.Title.Should().Be("Trailer");
        info.Duration.Should().Be(90);
        info.Formats.Should().ContainSingle().Which.Protocol.Should().Be("https");
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheTrailerFolder()
    {
        StoreInfo("{}");

        (await _cache.RemoveAsync(TrailerId)).Should().BeTrue();

        Directory.Exists(Path.Combine(_transcodeRoot, TrailerId)).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_NoFolder_IsAlreadyDone()
    {
        (await _cache.RemoveAsync(TrailerId)).Should().BeTrue();
    }
}
