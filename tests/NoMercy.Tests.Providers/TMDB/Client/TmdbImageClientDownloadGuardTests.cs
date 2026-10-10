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

using ImageMagick;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Storage;

namespace NoMercy.Tests.Providers.TMDB.Client;

/// <summary>
/// Guards the empty/slash-only image path case in TmdbImageClient.Download.
/// An entity with no poster/backdrop reaches Download with an empty path;
/// path.Replace("/", "") then collapses to an empty file name and the write
/// target becomes the "original" cache DIRECTORY itself, producing
/// "Access to the path '…/cache/images/original' is denied" at runtime. The
/// guard must short-circuit before any filesystem write.
/// </summary>
[Trait("Category", "Unit")]
[Collection("TmdbImageClient")]
public class TmdbImageClientDownloadGuardTests
{
    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("   ")]
    public async Task Download_EmptyOrSlashOnlyPath_NeverWritesToStorage(string path)
    {
        Mock<IStorage> storage = new(MockBehavior.Loose);
        storage
            .Setup(s => s.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        storage
            .Setup(s => s.CreateDirectoryAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        TmdbImageClient.Initialize(storage.Object);

        Task<MagickImage?>? download = TmdbImageClient.Download(path);
        if (download is not null)
            await download;

        // The whole point: an empty file name must NOT reach WriteAsync, which
        // would target the 'original' directory and throw access-denied.
        storage.Verify(
            s =>
                s.WriteAsync(It.IsAny<string>(), It.IsAny<byte[]>(), It.IsAny<CancellationToken>()),
            Times.Never,
            "an empty/slash-only image path must short-circuit before any write to the images cache"
        );
    }
}
