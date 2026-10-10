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

using System.Formats.Tar;
using System.IO.Compression;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem.SystemCalls;

/// <summary>
/// Archiving.ExtractArchive is the sole entry point for turning a downloaded
/// binary bundle (ffmpeg, yt-dlp, ...) into files on disk. These tests cover
/// the zip-slip containment guard: a malicious archive entry must never be
/// allowed to write outside the requested destination directory.
/// </summary>
public class ArchivingTests : IDisposable
{
    private readonly string _workDir;
    private readonly IStorage _storage;

    public ArchivingTests()
    {
        _workDir = Path.Combine(Path.GetTempPath(), "nm-archiving-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_workDir);

        LocalStorageDriver driver = new();
        _storage = new LocalStorage(driver, new([], driver));
    }

    public void Dispose()
    {
        if (Directory.Exists(_workDir))
            Directory.Delete(_workDir, recursive: true);
    }

    private static void WriteZipWithEntry(string zipPath, string entryName, string content)
    {
        using FileStream fileStream = File.Create(zipPath);
        using ZipArchive archive = new(fileStream, ZipArchiveMode.Create);
        ZipArchiveEntry entry = archive.CreateEntry(entryName);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    [Fact]
    public async Task ExtractArchive_ZipWithRelativeTraversalEntry_Throws()
    {
        string zipPath = Path.Combine(_workDir, "malicious.zip");
        string destination = Path.Combine(_workDir, "dest");
        Directory.CreateDirectory(destination);

        WriteZipWithEntry(zipPath, "../evil.txt", "payload");

        await Assert.ThrowsAsync<Exception>(() =>
            Archiving.ExtractArchive(_storage, zipPath, destination)
        );

        Assert.False(File.Exists(Path.Combine(_workDir, "evil.txt")));
    }

    [Fact]
    public async Task ExtractArchive_ZipWithDeepRelativeTraversalEntry_Throws()
    {
        string zipPath = Path.Combine(_workDir, "malicious-deep.zip");
        string destination = Path.Combine(_workDir, "dest2");
        Directory.CreateDirectory(destination);

        WriteZipWithEntry(zipPath, "..\\evil.txt", "payload");

        await Assert.ThrowsAsync<Exception>(() =>
            Archiving.ExtractArchive(_storage, zipPath, destination)
        );
    }

    [Fact]
    public async Task ExtractArchive_ZipWithAbsolutePathEntry_Throws()
    {
        string zipPath = Path.Combine(_workDir, "malicious-abs.zip");
        string destination = Path.Combine(_workDir, "dest3");
        Directory.CreateDirectory(destination);

        string outsideAbsolute = Path.Combine(_workDir, "outside", "evil.txt");
        WriteZipWithEntry(zipPath, outsideAbsolute, "payload");

        await Assert.ThrowsAsync<Exception>(() =>
            Archiving.ExtractArchive(_storage, zipPath, destination)
        );

        Assert.False(File.Exists(outsideAbsolute));
    }

    [Fact]
    public async Task ExtractArchive_ZipWithNormalEntry_ExtractsIntoDestination()
    {
        string zipPath = Path.Combine(_workDir, "normal.zip");
        string destination = Path.Combine(_workDir, "dest4");
        Directory.CreateDirectory(destination);

        WriteZipWithEntry(zipPath, "readme.txt", "hello world");

        List<string> extracted = await Archiving.ExtractArchive(_storage, zipPath, destination);

        Assert.Single(extracted);
        string extractedPath = Path.Combine(destination, "readme.txt");
        Assert.True(File.Exists(extractedPath));
        Assert.Equal("hello world", await File.ReadAllTextAsync(extractedPath));
    }

    [Fact]
    public async Task ExtractArchive_TgzFile_RoutesToTarBranchAndExtracts()
    {
        // Regression guard for the missing-leading-dot suffix bug: ".tgz" must
        // route to the tar branch (not "unsupported format") and actually
        // extract via the real `tar` binary.
        string sourceDir = Path.Combine(_workDir, "tgz-source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "hello.txt"), "hello tgz");

        string tgzPath = Path.Combine(_workDir, "bundle.tgz");
        await using (FileStream fileStream = File.Create(tgzPath))
        await using (
            GZipStream gzipStream = new(fileStream, CompressionMode.Compress, leaveOpen: true)
        )
        {
            await TarFile.CreateFromDirectoryAsync(
                sourceDir,
                gzipStream,
                includeBaseDirectory: false
            );
        }

        string destination = Path.Combine(_workDir, "dest-tgz");
        Directory.CreateDirectory(destination);

        List<string> extracted = await Archiving.ExtractArchive(_storage, tgzPath, destination);

        Assert.Contains(extracted, path => path.EndsWith("hello.txt"));
        Assert.Equal(
            "hello tgz",
            await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt"))
        );
    }

    [Fact]
    public async Task ExtractArchive_TgzFile_DestinationDirDoesNotExist_CreatesItAndExtracts()
    {
        // Regression guard: tar's `-C` target is not auto-created by the tar CLI
        // itself. On a fresh install the ffmpeg output folder is absent, so this
        // must create it before shelling out — otherwise tar aborts with
        // "Cannot open: No such file or directory" and strands BootStage.Binaries.
        string sourceDir = Path.Combine(_workDir, "tgz-nodest-source");
        Directory.CreateDirectory(sourceDir);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "hello.txt"), "hello tgz");

        string tgzPath = Path.Combine(_workDir, "bundle-nodest.tgz");
        await using (FileStream fileStream = File.Create(tgzPath))
        await using (
            GZipStream gzipStream = new(fileStream, CompressionMode.Compress, leaveOpen: true)
        )
        {
            await TarFile.CreateFromDirectoryAsync(
                sourceDir,
                gzipStream,
                includeBaseDirectory: false
            );
        }

        // Nested, non-existent destination — neither "dest-nodest" nor its
        // "leaf" child exist before extraction.
        string destination = Path.Combine(_workDir, "dest-nodest", "leaf");
        Assert.False(Directory.Exists(destination));

        List<string> extracted = await Archiving.ExtractArchive(_storage, tgzPath, destination);

        Assert.True(Directory.Exists(destination));
        Assert.Contains(extracted, path => path.EndsWith("hello.txt"));
        Assert.Equal(
            "hello tgz",
            await File.ReadAllTextAsync(Path.Combine(destination, "hello.txt"))
        );
    }

    [Fact]
    public async Task ExtractArchive_UnsupportedExtension_ReturnsEmptyList()
    {
        string filePath = Path.Combine(_workDir, "not-an-archive.txt");
        await File.WriteAllTextAsync(filePath, "not an archive");
        string destination = Path.Combine(_workDir, "dest5");
        Directory.CreateDirectory(destination);

        List<string> extracted = await Archiving.ExtractArchive(_storage, filePath, destination);

        Assert.Empty(extracted);
    }

    // -------------------------------------------------------------------------
    // Missing/incomplete archive guard: extraction must never be attempted
    // against a file that isn't actually (fully) on disk. Reproduces the
    // onboarding blocker where a failed/partial ffmpeg download reached `tar`
    // as a bare "No such file or directory" instead of a clear abort.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task ExtractArchive_MissingTarFile_ThrowsFileNotFoundWithoutInvokingTar()
    {
        string tarPath = Path.Combine(_workDir, "does-not-exist.tar.gz");
        string destination = Path.Combine(_workDir, "dest-missing-tar");
        Directory.CreateDirectory(destination);

        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Archiving.ExtractArchive(_storage, tarPath, destination)
        );

        Assert.Contains("missing or empty", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchive_MissingZipFile_ThrowsFileNotFoundWithoutInvokingZipFile()
    {
        string zipPath = Path.Combine(_workDir, "does-not-exist.zip");
        string destination = Path.Combine(_workDir, "dest-missing-zip");
        Directory.CreateDirectory(destination);

        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Archiving.ExtractArchive(_storage, zipPath, destination)
        );

        Assert.Contains("missing or empty", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }

    [Fact]
    public async Task ExtractArchive_ZeroByteTarFile_ThrowsFileNotFoundWithoutInvokingTar()
    {
        // A 0-byte file is what a killed/partial download or a verify step that
        // deleted-then-recreated an empty placeholder would leave behind — it
        // must never be handed to `tar`, which fails with an opaque exit code.
        string tarPath = Path.Combine(_workDir, "empty.tar.gz");
        await File.WriteAllBytesAsync(tarPath, []);
        string destination = Path.Combine(_workDir, "dest-empty-tar");
        Directory.CreateDirectory(destination);

        FileNotFoundException ex = await Assert.ThrowsAsync<FileNotFoundException>(() =>
            Archiving.ExtractArchive(_storage, tarPath, destination)
        );

        Assert.Contains("missing or empty", ex.Message);
        Assert.Empty(Directory.EnumerateFileSystemEntries(destination));
    }
}
