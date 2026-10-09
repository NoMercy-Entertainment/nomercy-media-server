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
using NoMercy.Encoder.Subtitles;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Encoder.Subtitles;

/// <summary>
/// Tests <see cref="TesseractModelManager"/> against a fake
/// <see cref="ITesseractModelDownloader"/> — the real signed-release verification lives in
/// NoMercy.Setup's TesseractModelDownloaderTests; this class asserts what the manager does
/// with the downloader's outcome (file written / not written / cleaned up), never that a
/// method was merely called.
/// </summary>
public class TesseractModelManagerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly EncoderOptions _options;

    public TesseractModelManagerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TessTest_{Guid.NewGuid():N}");
        _options = new() { FfmpegPathOverride = "ffmpeg", TesseractModelsDirectory = _tempDir };
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task Ensure_WhenModelExists_ReturnsPathWithoutNetwork()
    {
        Directory.CreateDirectory(_tempDir);
        string localPath = Path.Combine(_tempDir, "eng.traineddata");
        await File.WriteAllBytesAsync(localPath, [0xFF, 0xFE]);

        FakeTesseractModelDownloader downloader = new();
        TesseractModelManager manager = BuildManager(downloader);

        string resolved = await manager.EnsureLanguageModelAsync("eng", CancellationToken.None);

        Assert.Equal(localPath, resolved);
        Assert.Equal(0, downloader.CallCount);
    }

    [Fact]
    public async Task Ensure_WhenModelMissing_DownloadsVerifiedModelAndSaves()
    {
        byte[] payload = [0x01, 0x02, 0x03, 0x04];
        FakeTesseractModelDownloader downloader = new() { Payload = payload };
        TesseractModelManager manager = BuildManager(downloader);

        string resolved = await manager.EnsureLanguageModelAsync("fra", CancellationToken.None);

        string expectedPath = Path.Combine(_tempDir, "fra.traineddata");
        Assert.Equal(expectedPath, resolved);
        Assert.Equal(1, downloader.CallCount);
        Assert.True(File.Exists(expectedPath));
        Assert.Equal(payload, await File.ReadAllBytesAsync(expectedPath));
        Assert.False(File.Exists($"{expectedPath}.tmp"));
    }

    [Fact]
    public async Task Ensure_WhenManifestSignatureInvalid_RejectsAndWritesNothing()
    {
        // The downloader hard-fails when the signed manifest's signature does not verify —
        // the manager must not write anything, and must not fall back to any other source.
        FakeTesseractModelDownloader downloader = new()
        {
            FailureToThrow = new InvalidOperationException(
                "nomercy-tesseract release manifest signature could not be verified"
            ),
        };
        TesseractModelManager manager = BuildManager(downloader);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.EnsureLanguageModelAsync("deu", CancellationToken.None)
        );

        string expectedPath = Path.Combine(_tempDir, "deu.traineddata");
        Assert.False(File.Exists(expectedPath));
        Assert.False(File.Exists($"{expectedPath}.tmp"));
    }

    [Fact]
    public async Task Ensure_WhenShaMismatch_RejectsAndWritesNothing()
    {
        // The downloader hard-fails when the downloaded bytes don't match the signed
        // manifest's SHA-256 — the manager must not install the tampered/corrupt model.
        FakeTesseractModelDownloader downloader = new()
        {
            FailureToThrow = new InvalidDataException(
                "SHA-256 mismatch: the downloaded model does not match the signed manifest."
            ),
        };
        TesseractModelManager manager = BuildManager(downloader);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            manager.EnsureLanguageModelAsync("jpn", CancellationToken.None)
        );

        string expectedPath = Path.Combine(_tempDir, "jpn.traineddata");
        Assert.False(File.Exists(expectedPath));
        Assert.False(File.Exists($"{expectedPath}.tmp"));
    }

    [Fact]
    public async Task Ensure_EmptyLanguage_ThrowsArgumentException()
    {
        TesseractModelManager manager = BuildManager(new FakeTesseractModelDownloader());

        await Assert.ThrowsAsync<ArgumentException>(() =>
            manager.EnsureLanguageModelAsync("", CancellationToken.None)
        );
    }

    [Fact]
    public void GetDownloadedLanguages_WhenDirectoryMissing_ReturnsEmpty()
    {
        TesseractModelManager manager = BuildManager(new FakeTesseractModelDownloader());

        IReadOnlyList<string> langs = manager.GetDownloadedLanguages();

        Assert.Empty(langs);
    }

    [Fact]
    public async Task GetDownloadedLanguages_ListsAllTrainedData()
    {
        Directory.CreateDirectory(_tempDir);
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "eng.traineddata"), "data");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "fra.traineddata"), "data");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "readme.txt"), "not a model");

        TesseractModelManager manager = BuildManager(new FakeTesseractModelDownloader());

        IReadOnlyList<string> langs = manager.GetDownloadedLanguages();

        Assert.Contains("eng", langs);
        Assert.Contains("fra", langs);
        Assert.DoesNotContain("readme", langs);
    }

    [Fact]
    public async Task Ensure_WhenDownloadCancelled_DoesNotLeavePartialFile()
    {
        CancellationTokenSource cts = new();
        FakeTesseractModelDownloader downloader = new() { OnDownloadRequested = cts.Cancel };
        TesseractModelManager manager = BuildManager(downloader);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.EnsureLanguageModelAsync("deu", cts.Token)
        );

        string expectedPath = Path.Combine(_tempDir, "deu.traineddata");
        Assert.False(File.Exists(expectedPath));
        Assert.False(File.Exists($"{expectedPath}.tmp"));
    }

    private TesseractModelManager BuildManager(ITesseractModelDownloader downloader)
    {
        LocalStorageDriver driver = new();
        LocalStorage storage = new(driver, new([], driver));
        return new(_options, downloader, storage, NullLogger<TesseractModelManager>.Instance);
    }

    private sealed class FakeTesseractModelDownloader : ITesseractModelDownloader
    {
        public byte[] Payload { get; init; } = [];
        public Exception? FailureToThrow { get; init; }
        public Action? OnDownloadRequested { get; init; }
        public int CallCount { get; private set; }

        public Task<Stream> DownloadVerifiedAsync(string language, CancellationToken ct)
        {
            CallCount++;
            OnDownloadRequested?.Invoke();
            ct.ThrowIfCancellationRequested();

            if (FailureToThrow is not null)
                throw FailureToThrow;

            return Task.FromResult<Stream>(new MemoryStream(Payload));
        }
    }
}
