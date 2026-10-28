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
}
