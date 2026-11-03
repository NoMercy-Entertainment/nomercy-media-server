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

using System.Text;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// REQUIREMENT: a binary upgrade must never be blocked by its own backup file. On Windows
/// the image of a running executable can be renamed but not deleted, so a cloudflared that
/// is still running from the file an earlier upgrade renamed to <c>.bak</c> makes that
/// <c>.bak</c> undeletable ("Access to the path ... is denied"). The stale backup is
/// irrelevant to installing the new file, and the backup of the binary a live tunnel runs
/// from cannot be removed until that tunnel restarts — neither may fail the upgrade.
/// </summary>
[Trait("Category", "Unit")]
public sealed class BinariesBackupReplaceTests : IDisposable
{
    private const string AssetUrl = "https://example.com/cloudflared-windows-amd64.exe";
    private readonly string _tempDir;

    public BinariesBackupReplaceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"nomercy-bak-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public async Task Upgrade_WhenTheStaleBackupCannotBeDeleted_StillInstallsTheNewBinary()
    {
        string destPath = Path.Combine(_tempDir, "cloudflared.exe");
        await File.WriteAllTextAsync(destPath, "old");
        await File.WriteAllTextAsync(destPath + ".bak", "stale");
        Binaries binaries = BuildBinaries(out LockedBackupDriver driver);

        Exception? error = await Record.ExceptionAsync(() => Download(binaries, destPath));

        Assert.Null(error);
        Assert.Equal("new", await File.ReadAllTextAsync(destPath));
        Assert.Contains(destPath + ".bak", driver.RefusedDeletes);
    }

    [Fact]
    public async Task Upgrade_WhenThePreviousBinaryStaysInUse_KeepsItAsideAndDoesNotFail()
    {
        string destPath = Path.Combine(_tempDir, "cloudflared.exe");
        await File.WriteAllTextAsync(destPath, "old");
        Binaries binaries = BuildBinaries(out _);

        Exception? error = await Record.ExceptionAsync(() => Download(binaries, destPath));

        Assert.Null(error);
        Assert.Equal("new", await File.ReadAllTextAsync(destPath));

        // The binary a live tunnel runs from cannot be deleted; it is renamed out of the
        // way and swept on a later run, never left in place of the new one.
        string[] backups = Directory.GetFiles(_tempDir, "cloudflared.exe.bak*");
        string backup = Assert.Single(backups);
        Assert.Equal("old", await File.ReadAllTextAsync(backup));
    }

    private static Task Download(Binaries binaries, string destPath) =>
        binaries.DownloadWithVerificationAsync(
            "https://api.github.com/test",
            "cloudflared",
            new(AssetUrl),
            destPath,
            new()
            {
                TagName = "2026.10.5",
                PublishedAt = DateTimeOffset.UtcNow.AddDays(-30),
                Assets =
                [
                    new()
                    {
                        Name = "cloudflared-windows-amd64.exe",
                        BrowserDownloadUrl = new(AssetUrl),
                        Size = 3,
                        Digest = string.Empty,
                    },
                ],
            },
            "cloudflared-windows-amd64.exe",
            enforceSignedManifest: false
        );

    private static Binaries BuildBinaries(out LockedBackupDriver driver)
    {
        driver = new(new LocalStorageDriver());
        StoragePathGuard guard = new([], driver);
        LocalStorage storage = new(driver, guard);
        FakeHttpHandler handler = new();
        handler.Register(AssetUrl, Encoding.UTF8.GetBytes("new"));
        return new(driver, storage, new HttpClient(handler));
    }

    /// <summary>
    /// A local driver on which every backup file behaves like the image of a running
    /// executable on Windows: it can be renamed, but deleting it is refused.
    /// </summary>
    private sealed class LockedBackupDriver(IStorageDriver inner) : IStorageDriver
    {
        public List<string> RefusedDeletes { get; } = [];

        public bool FileExists(string path) => inner.FileExists(path);

        public bool DirectoryExists(string path) => inner.DirectoryExists(path);

        public void CreateDirectory(string path) => inner.CreateDirectory(path);

        public void DeleteFile(string path)
        {
            if (path.Contains(".bak", StringComparison.OrdinalIgnoreCase))
            {
                RefusedDeletes.Add(path);
                throw new UnauthorizedAccessException($"Access to the path '{path}' is denied.");
            }

            inner.DeleteFile(path);
        }

        public void DeleteDirectory(string path, bool recursive) =>
            inner.DeleteDirectory(path, recursive);

        public long GetFileSize(string path) => inner.GetFileSize(path);

        public DateTime GetLastWriteTimeUtc(string path) => inner.GetLastWriteTimeUtc(path);

        public DateTime GetCreationTimeUtc(string path) => inner.GetCreationTimeUtc(path);

        public DateTime GetLastAccessTimeUtc(string path) => inner.GetLastAccessTimeUtc(path);

        public Stream OpenRead(string path) => inner.OpenRead(path);

        public Stream OpenWrite(string path, bool overwrite) => inner.OpenWrite(path, overwrite);

        public void MoveFile(string source, string destination) =>
            inner.MoveFile(source, destination);

        public void CopyFile(string source, string destination, bool overwrite) =>
            inner.CopyFile(source, destination, overwrite);

        public IEnumerable<string> EnumerateFileSystemEntries(
            string directory,
            string searchPattern,
            SearchOption option
        ) => inner.EnumerateFileSystemEntries(directory, searchPattern, option);

        public string GetFullPath(string path) => inner.GetFullPath(path);

        public string? ResolveLinkTarget(string path) => inner.ResolveLinkTarget(path);

        public bool IsHidden(string path) => inner.IsHidden(path);

        public void MoveDirectory(string source, string destination) =>
            inner.MoveDirectory(source, destination);
    }
}
