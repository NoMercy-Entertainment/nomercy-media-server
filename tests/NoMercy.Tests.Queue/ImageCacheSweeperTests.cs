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
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Queue.MediaServer.Jobs;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using Xunit;

namespace NoMercy.Tests.Queue;

public class ImageCacheSweeperTests : IDisposable
{
    private const long FileBytes = 100;
    private static readonly TimeSpan Grace = TimeSpan.FromDays(14);

    private readonly string _root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private readonly ImageCacheSweeper _sweeper;

    public ImageCacheSweeperTests()
    {
        Directory.CreateDirectory(_root);
        LocalStorageDriver driver = new();
        IStorage storage = new LocalStorage(driver, new StoragePathGuard([_root], driver));
        _sweeper = new(storage, NullLogger<ImageCacheSweeper>.Instance);
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
    }

    private string Place(string folder, string name, int ageDays)
    {
        string directory = Path.Combine(_root, folder);
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, name);
        File.WriteAllBytes(path, new byte[FileBytes]);
        File.SetLastWriteTimeUtc(path, _now.UtcDateTime.AddDays(-ageDays));
        return path;
    }

    private ImageCacheSweepResult Sweep(
        IReadOnlySet<string>? referenced = null,
        long maxBytes = 0,
        TimeSpan? grace = null
    )
    {
        return _sweeper.Sweep(
            _root,
            referenced ?? new HashSet<string>(),
            maxBytes,
            grace ?? Grace,
            _now
        );
    }

    [Fact]
    public void Sweep_OverTheLimit_EvictsLeastRecentlyUsedFirstAndEndsUnderTheLimit()
    {
        string oldest = Place("original", "a.jpg", 40);
        string older = Place("original", "b.jpg", 30);
        string newer = Place("original", "c.jpg", 20);
        string newest = Place("original", "d.jpg", 10);
        HashSet<string> referenced = ["a.jpg", "b.jpg", "c.jpg", "d.jpg"];

        ImageCacheSweepResult result = Sweep(referenced, maxBytes: 250);

        File.Exists(oldest).Should().BeFalse();
        File.Exists(older).Should().BeFalse();
        File.Exists(newer).Should().BeTrue();
        File.Exists(newest).Should().BeTrue();
        result.EvictedFiles.Should().Be(2);
        result.BytesAfter.Should().Be(2 * FileBytes).And.BeLessThanOrEqualTo(250);
    }

    [Fact]
    public void Sweep_ServedFileWithOldDownload_SurvivesBeforeAnUnservedNewerFile()
    {
        string servedRecently = Place("original", "served.jpg", 1);
        string neverServed = Place("original", "idle.jpg", 20);
        HashSet<string> referenced = ["served.jpg", "idle.jpg"];

        Sweep(referenced, maxBytes: FileBytes);

        File.Exists(servedRecently).Should().BeTrue();
        File.Exists(neverServed).Should().BeFalse();
    }

    [Fact]
    public void Sweep_UnderTheLimit_RemovesNothing()
    {
        string kept = Place("original", "a.jpg", 40);
        HashSet<string> referenced = ["a.jpg"];

        ImageCacheSweepResult result = Sweep(referenced, maxBytes: 1000);

        File.Exists(kept).Should().BeTrue();
        result.EvictedFiles.Should().Be(0);
    }

    [Fact]
    public void Sweep_NoLimit_NeverEvicts()
    {
        string kept = Place("original", "a.jpg", 40);
        HashSet<string> referenced = ["a.jpg"];

        Sweep(referenced, maxBytes: 0);

        File.Exists(kept).Should().BeTrue();
    }

    [Fact]
    public void Sweep_ImageAnItemStillRefersTo_IsNotRemovedAsOrphan()
    {
        string used = Place("original", "poster.jpg", 100);
        HashSet<string> referenced = ["poster.jpg"];

        ImageCacheSweepResult result = Sweep(referenced);

        File.Exists(used).Should().BeTrue();
        result.OrphansRemoved.Should().Be(0);
    }

    [Fact]
    public void Sweep_ImageNoItemRefersTo_IsRemovedAsOrphan()
    {
        string orphan = Place("original", "gone.jpg", 100);
        string used = Place("original", "poster.jpg", 100);
        HashSet<string> referenced = ["poster.jpg"];

        ImageCacheSweepResult result = Sweep(referenced);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(used).Should().BeTrue();
        result.OrphansRemoved.Should().Be(1);
        result.OrphanBytes.Should().Be(FileBytes);
    }

    [Fact]
    public void Sweep_OrphanYoungerThanTheGrace_IsKept()
    {
        string fresh = Place("original", "just-downloaded.jpg", 1);

        Sweep();

        File.Exists(fresh).Should().BeTrue();
    }

    [Fact]
    public void Sweep_MusicFolder_IsNeverTouched()
    {
        string cover = Place("music", "cover.jpg", 400);

        ImageCacheSweepResult result = Sweep(maxBytes: 1);

        File.Exists(cover).Should().BeTrue();
        result.OrphansRemoved.Should().Be(0);
        result.EvictedFiles.Should().Be(0);
    }

    [Fact]
    public void Sweep_TempRendition_IsNotAnOrphanButIsEvictedWhenOverTheLimit()
    {
        string rendition = Place("temp", "0123abcd.webp", 100);

        Sweep(maxBytes: 1000);
        File.Exists(rendition).Should().BeTrue();

        Sweep(maxBytes: 1);
        File.Exists(rendition).Should().BeFalse();
    }

    [Fact]
    public void Sweep_OrphansGoBeforeAnyReferencedFileIsEvicted()
    {
        string orphan = Place("original", "orphan.jpg", 30);
        string referencedOld = Place("original", "old.jpg", 50);
        string referencedNew = Place("original", "new.jpg", 5);
        HashSet<string> referenced = ["old.jpg", "new.jpg"];

        ImageCacheSweepResult result = Sweep(referenced, maxBytes: 2 * FileBytes);

        File.Exists(orphan).Should().BeFalse();
        File.Exists(referencedOld).Should().BeTrue();
        File.Exists(referencedNew).Should().BeTrue();
        result.EvictedFiles.Should().Be(0);
    }

    [Fact]
    public void Sweep_NeverRemovesAFolder()
    {
        Place("original", "a.jpg", 100);

        Sweep(maxBytes: 1);

        Directory.Exists(Path.Combine(_root, "original")).Should().BeTrue();
    }
}
