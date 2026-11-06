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
using Moq;
using NoMercy.Encoder.Orchestration;
using NoMercy.Encoder.Pipeline;
using NoMercy.Encoder.Progress;
using NoMercy.Encoder.Strategies;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.Orchestration;

/// <summary>
/// Publishing the encoder output into the library folder holds that folder in the
/// <see cref="ServerWriteLedger"/>, so the watcher does not import the server's own files.
/// </summary>
public sealed class EncodingOrchestratorPublishLedgerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        $"nm-publish-{Guid.NewGuid():N}"
    );
    private readonly IStorage _storage = new LocalStorage(
        new LocalStorageDriver(),
        new([], new LocalStorageDriver())
    );

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch
        {
            // best-effort cleanup
        }
    }

    private EncodingOrchestrator Build() =>
        new(
            new Mock<IStrategyResolver>().Object,
            _storage,
            new Mock<IEncoder>().Object,
            NullLogger<EncodingOrchestrator>.Instance
        );

    [Fact]
    public async Task PublishTempDirAsync_holds_the_destination_folder_while_it_publishes_and_after()
    {
        string tempDir = Path.Combine(_root, "cache", "Synthetic Show", "Synthetic Episode");
        string outputDir = Path.Combine(_root, "library", "Synthetic Show", "Synthetic Episode");
        Directory.CreateDirectory(tempDir);
        File.WriteAllText(Path.Combine(tempDir, "Synthetic Episode.m3u8"), "#EXTM3U");

        bool heldWhilePublishing = false;
        Mock<IProgressObserver> observer = new();
        observer
            .Setup(o => o.OnStageStarted(It.IsAny<string>()))
            .Callback(() =>
                heldWhilePublishing = ServerWriteLedger.Shared.IsServerWritten(
                    Path.Combine(outputDir, "Synthetic Episode.m3u8")
                )
            );

        await Build().PublishTempDirAsync(tempDir, outputDir, _storage, observer.Object, default);

        heldWhilePublishing.Should().BeTrue();
        File.Exists(Path.Combine(outputDir, "Synthetic Episode.m3u8")).Should().BeTrue();
        ServerWriteLedger
            .Shared.IsServerWritten(Path.Combine(outputDir, "Synthetic Episode.m3u8"))
            .Should()
            .BeTrue();
        ServerWriteLedger
            .Shared.IsServerWritten(Path.Combine(_root, "library", "Other Show", "x.mkv"))
            .Should()
            .BeFalse();
    }

    // 2026-11-07: an episode with 18,062 output files published to an SMB library at
    // 18 files a minute, because every file paid its round trips one after another.
    [Fact]
    public async Task PublishTempDirAsync_copies_files_side_by_side_and_creates_each_folder_once()
    {
        string tempDir = Path.Combine(_root, "cache", "Synthetic Episode");
        for (int i = 0; i < 40; i++)
        {
            string folder = Path.Combine(tempDir, i % 2 == 0 ? "video" : "subtitles");
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, $"segment-{i:D3}.ts"), "data");
        }

        int createDirectoryCalls = 0;
        int openWrites = 0;
        int peakOpenWrites = 0;
        Mock<IStorage> destination = new() { DefaultValue = DefaultValue.Mock };
        destination
            .Setup(d => d.GetParent(It.IsAny<string>()))
            .Returns<string>(path => path[..path.LastIndexOf('/')]);
        destination
            .Setup(d => d.CreateDirectory(It.IsAny<string>()))
            .Callback(() => Interlocked.Increment(ref createDirectoryCalls));
        destination
            .Setup(d => d.OpenWriteAsync(It.IsAny<string>(), true, It.IsAny<CancellationToken>()))
            .Returns(async () =>
            {
                int now = Interlocked.Increment(ref openWrites);
                InterlockedMax(ref peakOpenWrites, now);
                await Task.Delay(20);
                return new CountingStream(() => Interlocked.Decrement(ref openWrites));
            });
        destination
            .Setup(d => d.ExistsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        await Build()
            .PublishTempDirAsync(tempDir, "Synthetic Episode", destination.Object, null, default);

        destination.Verify(
            d => d.MoveAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Exactly(40)
        );
        createDirectoryCalls.Should().Be(2);
        peakOpenWrites.Should().BeGreaterThan(1);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int current;
        while (value > (current = Volatile.Read(ref target)))
            Interlocked.CompareExchange(ref target, value, current);
    }

    private sealed class CountingStream(Action onDispose) : MemoryStream
    {
        protected override void Dispose(bool disposing)
        {
            onDispose();
            base.Dispose(disposing);
        }
    }
}
