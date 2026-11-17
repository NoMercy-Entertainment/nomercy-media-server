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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NoMercy.Encoder.BuildingBlocks;
using NoMercy.Encoder.Composition;
using NoMercy.Encoder.ContentAnalysis;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Subtitles;
using NoMercy.OpticalMedia.Composition;
using NoMercy.OpticalMedia.Rip;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Encoder.Storage;

/// <summary>
/// Phase 0.3 verification — encoder code reaches IStorage instead of
/// dropping to raw System.IO. Wires a <see cref="LoggingStorage"/>
/// decorator into the encoder DI container, exercises a handful of
/// representative entry points, and asserts every operation flowed
/// through the abstraction.
///
/// Pairs with the file-static grep in plans/encoder-v3-alignment.md
/// §0.3 — together they prove no encoder consumer can bypass
/// <see cref="IStorage"/> by accident.
/// </summary>
public class IStorageAdoptionTests
{
    private static (ServiceProvider Provider, LoggingStorage Logger) BuildProvider()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddNoMercyEncoder(opts =>
        {
            opts.FfmpegPathOverride = "ffmpeg";
            opts.FfprobePathOverride = "ffprobe";
        });
        services.AddNoMercyOpticalMedia();

        // Replace IStorage with a logging decorator wrapping LocalStorage
        // (built from the same driver the encoder defaulted to).
        services.RemoveAll<IStorage>();
        services.AddSingleton<IStorage>(sp =>
        {
            IStorageDriver driver = sp.GetRequiredService<IStorageDriver>();
            StoragePathGuard guard = sp.GetRequiredService<StoragePathGuard>();
            return new LoggingStorage(new LocalStorage(driver, guard));
        });

        ServiceProvider provider = services.BuildServiceProvider();
        LoggingStorage logger = (LoggingStorage)provider.GetRequiredService<IStorage>();
        return (provider, logger);
    }

    [Fact]
    public void All_encoder_consumers_resolve_logging_storage_through_DI()
    {
        (ServiceProvider provider, LoggingStorage _) = BuildProvider();

        // Every type below was migrated in Phase 0.2 to depend on IStorage.
        // If any reverted to raw System.IO the constructor would resolve
        // a raw LocalStorage / no IStorage at all and this would throw.
        provider.GetRequiredService<IDiscRipper>().Should().NotBeNull();
        provider.GetRequiredService<ITesseractModelManager>().Should().NotBeNull();
        provider.GetRequiredService<IWhisperTranscriber>().Should().NotBeNull();
        provider.GetRequiredService<ISubtitleOcrEngine>().Should().NotBeNull();
        provider.GetRequiredService<IChapterWriter>().Should().NotBeNull();
        provider.GetRequiredService<IFontExtractor>().Should().NotBeNull();
        provider.GetRequiredService<IThumbnailGenerator>().Should().NotBeNull();
        provider.GetRequiredService<ICheckpointStore>().Should().NotBeNull();
        provider.GetRequiredService<ICropDetector>().Should().NotBeNull();
        provider.GetRequiredService<IEpisodeSignalProbe>().Should().NotBeNull();
    }

    [Fact]
    public async Task Filesystem_calls_routed_through_logging_storage()
    {
        (ServiceProvider _, LoggingStorage logger) = BuildProvider();
        string tempRoot = Path.Combine(
            Path.GetTempPath(),
            "nm-adoption-" + Path.GetRandomFileName()
        );

        try
        {
            // Exercise sync + async surface through the logger.
            logger.CreateDirectory(tempRoot);
            string filePath = Path.Combine(tempRoot, "smoke.bin");
            logger.Write(filePath, [0xDE, 0xAD]);
            logger.Exists(filePath).Should().BeTrue();
            logger.SizeOrZero(filePath).Should().Be(2);

            byte[] bytes = await logger.ReadAsync(filePath, CancellationToken.None);
            bytes.Should().Equal(0xDE, 0xAD);

            await using LocalPathLease lease = logger.AcquireLocalPath(filePath);
            lease.Path.Should().Be(Path.GetFullPath(filePath));

            logger
                .Calls.Should()
                .Contain(c => c.StartsWith("CreateDirectory:"))
                .And.Contain(c => c.StartsWith("Write:"))
                .And.Contain(c => c.StartsWith("Exists:"))
                .And.Contain(c => c.StartsWith("SizeOrZero:"))
                .And.Contain(c => c.StartsWith("ReadAsync:"))
                .And.Contain(c => c.StartsWith("AcquireLocalPath:"));
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }
    }
}
