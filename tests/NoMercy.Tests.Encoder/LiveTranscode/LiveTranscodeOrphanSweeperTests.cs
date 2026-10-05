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
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.LiveTranscode;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.LiveTranscode;

public class LiveTranscodeOrphanSweeperTests : IDisposable
{
    private readonly string _cacheRoot = Path.Combine(
        Path.GetTempPath(),
        "nomercy-orphan-test-" + Ulid.NewUlid()
    );

    public void Dispose()
    {
        if (Directory.Exists(_cacheRoot))
            Directory.Delete(_cacheRoot, recursive: true);
    }

    private static IStorage MakeStorage() =>
        new LocalStorage(
            new LocalStorageDriver(),
            new([], new LocalStorageDriver())
        );

    private LiveTranscodeOrphanSweeper BuildSweeper()
    {
        EncoderOptions opts = new() { LiveTranscodeCachePath = _cacheRoot };
        return new(
            opts,
            NullLogger<LiveTranscodeOrphanSweeper>.Instance,
            MakeStorage()
        );
    }

    // ──────────────────────────────────────────────────────────────────────────
    // lts-* directories are deleted on startup
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_DeletesLtsPrefixedDirectories()
    {
        Directory.CreateDirectory(_cacheRoot);
        string orphan1 = Path.Combine(_cacheRoot, "lts-01J0000000000000000000000A");
        string orphan2 = Path.Combine(_cacheRoot, "lts-01J0000000000000000000000B");
        Directory.CreateDirectory(orphan1);
        Directory.CreateDirectory(orphan2);

        LiveTranscodeOrphanSweeper sweeper = BuildSweeper();
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(orphan1).Should().BeFalse();
        Directory.Exists(orphan2).Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Non-lts directories are preserved
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_PreservesNonLtsDirectories()
    {
        Directory.CreateDirectory(_cacheRoot);
        string keepDir = Path.Combine(_cacheRoot, "thumbnails");
        Directory.CreateDirectory(keepDir);

        LiveTranscodeOrphanSweeper sweeper = BuildSweeper();
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(keepDir).Should().BeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Files inside lts-* directory are also removed (recursive delete)
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_RecursivelyDeletesOrphanContents()
    {
        Directory.CreateDirectory(_cacheRoot);
        string orphanDir = Path.Combine(_cacheRoot, "lts-01J0000000000000000000000C");
        Directory.CreateDirectory(orphanDir);
        string segFile = Path.Combine(orphanDir, "seg_00001.ts");
        await File.WriteAllBytesAsync(segFile, [0x00, 0x01, 0x02]);

        LiveTranscodeOrphanSweeper sweeper = BuildSweeper();
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(orphanDir).Should().BeFalse();
        File.Exists(segFile).Should().BeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Missing cache root — no throw
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_MissingCacheRoot_DoesNotThrow()
    {
        // Use a path that definitely doesn't exist.
        EncoderOptions opts = new()
        {
            LiveTranscodeCachePath = Path.Combine(
                Path.GetTempPath(),
                "nomercy-does-not-exist-" + Ulid.NewUlid()
            ),
        };

        LiveTranscodeOrphanSweeper sweeper = new(
            opts,
            NullLogger<LiveTranscodeOrphanSweeper>.Instance,
            MakeStorage()
        );

        Func<Task> act = () => sweeper.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Empty cache root — no throw
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_EmptyCacheRoot_DoesNotThrow()
    {
        Directory.CreateDirectory(_cacheRoot);

        LiveTranscodeOrphanSweeper sweeper = BuildSweeper();

        Func<Task> act = () => sweeper.StartAsync(CancellationToken.None);
        await act.Should().NotThrowAsync();
    }

    // ──────────────────────────────────────────────────────────────────────────
    // Bug 1 — sweeper glob matches the lts-{ulid} format LiveEncoder creates
    // ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StartAsync_DeletesDirectoryNamedLtsFollowedByUlid()
    {
        Directory.CreateDirectory(_cacheRoot);
        string sessionId = Ulid.NewUlid().ToString();
        string sessionDir = Path.Combine(_cacheRoot, $"lts-{sessionId}");
        Directory.CreateDirectory(sessionDir);

        LiveTranscodeOrphanSweeper sweeper = BuildSweeper();
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(sessionDir).Should().BeFalse();
    }
}
