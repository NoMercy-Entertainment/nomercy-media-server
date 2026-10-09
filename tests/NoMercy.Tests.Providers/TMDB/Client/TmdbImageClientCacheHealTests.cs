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
using NoMercy.NmSystem.Information;
using NoMercy.Providers.TMDB.Client;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Providers.TMDB.Client;

/// <summary>
/// A 200 response from image.tmdb.org can still carry a non-image body (an HTML
/// error page, a truncated CDN response). The client used to persist those bytes
/// before decoding, poisoning the cache: the bad file then satisfied the Exists
/// check on every later scan and the decode failed forever. The cached-load path
/// must discard an undecodable cache entry so the next run re-downloads.
/// </summary>
[Trait("Category", "Unit")]
[Collection("TmdbImageClient")]
public class TmdbImageClientCacheHealTests
{
    private static IStorage RealScopedStorage()
    {
        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([AppFiles.AppPath], driver);
        return new LocalStorage(driver, guard);
    }

    [Fact]
    public async Task Download_UndecodableCachedFile_IsDeletedSoItCanBeRefetched()
    {
        IStorage storage = RealScopedStorage();
        TmdbImageClient.Initialize(storage);

        string folder = Path.Join(AppFiles.ImagesPath, "original");
        Directory.CreateDirectory(folder);

        // A poison cache entry: a .jpg name holding a non-image body, exactly the
        // shape an older build would have written from a bad 200 response.
        const string fileName = "poison-cache-heal-test.jpg";
        string filePath = Path.Join(folder, fileName);
        await File.WriteAllTextAsync(filePath, "<html><body>Not Found</body></html>");

        File.Exists(filePath).Should().BeTrue("the poison file is seeded before the call");

        // Download is keyed by "/{fileName}" -> path.Replace("/","") == fileName.
        // After discarding the poison entry the client re-downloads; with no
        // real HTTP backend configured that GET throws, which is fine — the
        // behavior under test is that the poison cache entry was discarded
        // before the re-fetch. Swallow the expected network failure.
        try
        {
            Task<MagickImage?>? download = TmdbImageClient.Download("/" + fileName);
            if (download is not null)
                await download;
        }
        catch (InvalidOperationException)
        {
            // No BaseAddress on the test HttpClient — the re-download leg fails
            // after the poison entry was already deleted. Expected.
        }

        // The poison bytes must no longer be the cache entry. The self-heal path
        // deletes the undecodable file before re-downloading, so it is gone (the
        // failed re-fetch wrote nothing) — never left as the original HTML poison,
        // which would make every later scan fail forever.
        File.Exists(filePath)
            .Should()
            .BeFalse(
                "the undecodable poison cache entry must be discarded so the next run re-downloads a clean copy"
            );
    }
}
