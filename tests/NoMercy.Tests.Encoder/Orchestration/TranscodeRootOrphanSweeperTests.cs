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
using NoMercy.Encoder.Orchestration;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.Orchestration;

public class TranscodeRootOrphanSweeperTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "nomercy-transcoderoot-test-" + Ulid.NewUlid()
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static IStorage MakeStorage() =>
        new LocalStorage(new LocalStorageDriver(), new([], new LocalStorageDriver()));

    private sealed class FakeOwners(params string[] owned) : IEncoderTempDirectoryOwners
    {
        public Task<IReadOnlyCollection<string>> GetOwnedTempDirectoriesAsync(
            CancellationToken ct
        ) => Task.FromResult<IReadOnlyCollection<string>>(owned);
    }

    private sealed class ThrowingOwners : IEncoderTempDirectoryOwners
    {
        public Task<IReadOnlyCollection<string>> GetOwnedTempDirectoriesAsync(
            CancellationToken ct
        ) => throw new InvalidOperationException("queue database unavailable");
    }

    private TranscodeRootOrphanSweeper BuildSweeper(
        bool sweepAllChildren,
        TimeSpan? minOrphanAge = null,
        IEncoderTempDirectoryOwners? owners = null
    ) =>
        new(
            NullLogger<TranscodeRootOrphanSweeper>.Instance,
            MakeStorage(),
            _root,
            sweepAllChildren,
            // Zero unless a test opts into age-gating: these existing cases assert
            // on prefix/pattern selection and write their fixture files immediately
            // before StartAsync runs, so any positive grace window would make every
            // one of them look "recent" and never reach the delete branch at all.
            minOrphanAge ?? TimeSpan.Zero,
            owners
        );

    [Fact]
    public async Task StartAsync_FallbackMode_DeletesOnlyEncPrefixedDirs()
    {
        Directory.CreateDirectory(_root);
        string enc1 = Path.Combine(_root, "nomercy-enc-01J0000000000000000000000A");
        string enc2 = Path.Combine(_root, "nomercy-enc-01J0000000000000000000000B");
        string unrelatedDir = Path.Combine(_root, "some-other-app-cache");
        string unrelatedFile = Path.Combine(_root, "unrelated.tmp");
        Directory.CreateDirectory(enc1);
        Directory.CreateDirectory(enc2);
        Directory.CreateDirectory(unrelatedDir);
        await File.WriteAllTextAsync(unrelatedFile, "keep me");

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(sweepAllChildren: false);
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(enc1).Should().BeFalse();
        Directory.Exists(enc2).Should().BeFalse();
        Directory.Exists(unrelatedDir).Should().BeTrue("fallback mode must not touch foreign dirs");
        File.Exists(unrelatedFile).Should().BeTrue("fallback mode must not touch foreign files");
    }

    [Fact]
    public async Task StartAsync_DedicatedMode_DeletesAllChildDirsIncludingMirroredShowDirs()
    {
        Directory.CreateDirectory(_root);
        string enc = Path.Combine(_root, "nomercy-enc-01J0000000000000000000000A");
        string mirroredShow = Path.Combine(_root, "The Show.(2021)");
        string strayFile = Path.Combine(_root, "leftover.log");
        Directory.CreateDirectory(enc);
        Directory.CreateDirectory(mirroredShow);
        await File.WriteAllTextAsync(strayFile, "log");

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(sweepAllChildren: true);
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(enc).Should().BeFalse();
        Directory
            .Exists(mirroredShow)
            .Should()
            .BeFalse("dedicated cache sweep must reclaim mirrored show dirs too");
        File.Exists(strayFile).Should().BeTrue("only directories are swept");
    }

    [Fact]
    public async Task StartAsync_MissingRoot_DoesNotThrow()
    {
        TranscodeRootOrphanSweeper sweeper = BuildSweeper(sweepAllChildren: true);

        Func<Task> act = () => sweeper.StartAsync(CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task StartAsync_DirWithRecentWrite_SurvivesTheSweep()
    {
        // Reproduces the incident: a deliberate restart during a live encode finds
        // a working directory ffmpeg wrote to seconds ago. "No encode is in flight
        // at boot" does not hold here, and the old unconditional sweep destroyed it.
        Directory.CreateDirectory(_root);
        string liveShow = Path.Combine(_root, "Frieren.Beyond.Journey's.End.(2023)");
        Directory.CreateDirectory(liveShow);
        await File.WriteAllTextAsync(Path.Combine(liveShow, "segment003.ts"), "live output");

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(
            sweepAllChildren: true,
            minOrphanAge: TimeSpan.FromMinutes(15)
        );
        await sweeper.StartAsync(CancellationToken.None);

        Directory
            .Exists(liveShow)
            .Should()
            .BeTrue("a directory written to inside the grace window is not an orphan");
    }

    [Fact]
    public async Task StartAsync_DirOwnedByAQueuedJob_SurvivesTheSweep()
    {
        // A queued job whose ffmpeg has not written for over an hour (waiting, or
        // suspended by the throttle) still owns its folder: the mtime grace alone
        // would delete it.
        Directory.CreateDirectory(_root);
        string ownedShow = Path.Combine(_root, "Frieren.(2023)");
        string ownedEpisode = Path.Combine(ownedShow, "Frieren.S01E01");
        string unownedShow = Path.Combine(_root, "Crashed.Show.(2020)");
        Directory.CreateDirectory(ownedEpisode);
        Directory.CreateDirectory(unownedShow);
        string ownedFile = Path.Combine(ownedEpisode, "video_00000.ts");
        string unownedFile = Path.Combine(unownedShow, "video_00000.ts");
        await File.WriteAllTextAsync(ownedFile, "segment");
        await File.WriteAllTextAsync(unownedFile, "segment");
        File.SetLastWriteTimeUtc(ownedFile, DateTime.UtcNow - TimeSpan.FromHours(1));
        File.SetLastWriteTimeUtc(unownedFile, DateTime.UtcNow - TimeSpan.FromHours(1));

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(
            sweepAllChildren: true,
            minOrphanAge: TimeSpan.FromMinutes(15),
            owners: new FakeOwners(ownedEpisode)
        );
        await sweeper.StartAsync(CancellationToken.None);

        Directory.Exists(ownedEpisode).Should().BeTrue("a queued job owns this folder");
        File.Exists(ownedFile).Should().BeTrue();
        Directory.Exists(unownedShow).Should().BeFalse("no job owns it and it is stale");
    }

    [Fact]
    public async Task StartAsync_OwnerLookupFails_KeepsEveryDir()
    {
        Directory.CreateDirectory(_root);
        string staleShow = Path.Combine(_root, "Crashed.Show.(2020)");
        Directory.CreateDirectory(staleShow);
        string staleFile = Path.Combine(staleShow, "video_00000.ts");
        await File.WriteAllTextAsync(staleFile, "segment");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow - TimeSpan.FromHours(1));

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(
            sweepAllChildren: true,
            minOrphanAge: TimeSpan.FromMinutes(15),
            owners: new ThrowingOwners()
        );
        await sweeper.StartAsync(CancellationToken.None);

        Directory
            .Exists(staleShow)
            .Should()
            .BeTrue("without the queue's answer the sweep cannot tell an orphan from a live job");
    }

    [Fact]
    public async Task StartAsync_DirWithOnlyStaleWrites_IsStillSwept()
    {
        Directory.CreateDirectory(_root);
        string crashedShow = Path.Combine(_root, "Crashed.Show.(2020)");
        Directory.CreateDirectory(crashedShow);
        string staleFile = Path.Combine(crashedShow, "segment001.ts");
        await File.WriteAllTextAsync(staleFile, "abandoned output");
        File.SetLastWriteTimeUtc(staleFile, DateTime.UtcNow - TimeSpan.FromHours(1));

        TranscodeRootOrphanSweeper sweeper = BuildSweeper(
            sweepAllChildren: true,
            minOrphanAge: TimeSpan.FromMinutes(15)
        );
        await sweeper.StartAsync(CancellationToken.None);

        Directory
            .Exists(crashedShow)
            .Should()
            .BeFalse("nothing in it was written within the grace window, so it is a real orphan");
    }
}
