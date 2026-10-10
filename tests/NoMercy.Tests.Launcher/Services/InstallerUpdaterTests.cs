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

using System.Net;
using System.Security.Cryptography;
using NoMercy.Launcher.Services;
using Xunit;

namespace NoMercy.Tests.Launcher.Services;

/// <summary>
/// <see cref="InstallerUpdater"/> tests keep their cache under the test output
/// directory. Each cache-file test uses a unique version and removes its files.
/// </summary>
public sealed class InstallerUpdaterTests
{
    [Fact]
    public async Task DownloadInstallerAsync_CorruptCachedInstaller_DeletesCacheAndDownloadsAgain()
    {
        string version = $"test-{Guid.NewGuid():N}";
        string cacheDir = Path.Combine(
            AppContext.BaseDirectory,
            $"installer-cache-{Guid.NewGuid():N}"
        );
        string exePath = Path.Combine(cacheDir, InstallerFileName(version));
        string sha256Path = exePath + ".sha256";
        byte[] freshContent = [.. "fresh installer content"u8];
        string hash = Convert.ToHexString(SHA256.HashData(freshContent));
        List<string> requests = [];
        bool staleFilesGoneBeforeRequest = false;

        try
        {
            Directory.CreateDirectory(cacheDir);
            await File.WriteAllBytesAsync(exePath, [.. "corrupt cached content"u8]);
            await File.WriteAllTextAsync(sha256Path, hash);

            using HttpClient httpClient = new(
                new StubHttpMessageHandler(request =>
                {
                    requests.Add(request.RequestUri!.AbsoluteUri);
                    if (requests.Count == 1)
                        staleFilesGoneBeforeRequest =
                            !File.Exists(exePath) && !File.Exists(sha256Path);

                    HttpContent content = request.RequestUri!.AbsoluteUri.EndsWith(".sha256")
                        ? new StringContent(hash)
                        : new ByteArrayContent(freshContent);
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
                })
            );
            InstallerUpdater updater = new(new ServerConnection(), httpClient, cacheDir);

            bool downloaded = await updater.DownloadInstallerAsync(version);

            downloaded.Should().BeTrue();
            staleFilesGoneBeforeRequest.Should().BeTrue();
            requests.Should().HaveCount(2);
            (await File.ReadAllBytesAsync(exePath)).Should().Equal(freshContent);
            (await File.ReadAllTextAsync(sha256Path)).Should().Be(hash);
            (await updater.VerifyInstallerAsync(version)).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(cacheDir))
                Directory.Delete(cacheDir, true);
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond
    ) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => Task.FromResult(respond(request));
    }

    private static string CacheDir =>
        Path.Combine(AppContext.BaseDirectory, "InstallerUpdaterTestsCache");

    private static string InstallerFileName(string version) =>
        $"NoMercyMediaServer-{version}-windows-x64-setup.exe";

    [Fact]
    public async Task IsInstallerDeploymentAsync_ProcessRunningOutsideBinariesPath_ReturnsTrue()
    {
        // The test host process (testhost.exe / dotnet) never lives under
        // %AppData%\NoMercy\binaries, so this must report "installer deployment".
        InstallerUpdater updater = new(new ServerConnection(), CacheDir);

        bool result = await updater.IsInstallerDeploymentAsync();

        result.Should().BeTrue();
    }

    [Fact]
    public async Task IsInstallerDeploymentAsync_InstallDirEnvVarSet_ReturnsTrueWithoutPathCheck()
    {
        Environment.SetEnvironmentVariable("NOMERCY_INSTALL_DIR", @"C:\Program Files\NoMercy");
        try
        {
            InstallerUpdater updater = new(new ServerConnection(), CacheDir);

            bool result = await updater.IsInstallerDeploymentAsync();

            result.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable("NOMERCY_INSTALL_DIR", null);
        }
    }

    [Fact]
    public async Task VerifyInstallerAsync_NoSha256Sidecar_ReturnsTrueAsLegacyRelease()
    {
        string version = $"test-{Guid.NewGuid():N}";
        InstallerUpdater updater = new(new ServerConnection(), CacheDir);

        bool result = await updater.VerifyInstallerAsync(version);

        result.Should().BeTrue();
    }

    [Fact]
    public async Task VerifyInstallerAsync_MatchingSha256Sidecar_ReturnsTrue()
    {
        string version = $"test-{Guid.NewGuid():N}";
        Directory.CreateDirectory(CacheDir);
        string exePath = Path.Combine(CacheDir, InstallerFileName(version));
        string sha256Path = exePath + ".sha256";

        try
        {
            byte[] content = [.. "fake installer bytes for hash verification"u8];
            await File.WriteAllBytesAsync(exePath, content);
            string hash = Convert.ToHexString(SHA256.HashData(content));
            await File.WriteAllTextAsync(sha256Path, hash);

            InstallerUpdater updater = new(new ServerConnection(), CacheDir);

            bool result = await updater.VerifyInstallerAsync(version);

            result.Should().BeTrue();
        }
        finally
        {
            File.Delete(exePath);
            File.Delete(sha256Path);
        }
    }

    [Fact]
    public async Task VerifyInstallerAsync_SidecarInHashSpaceFilenameFormat_StillParsesTheHash()
    {
        string version = $"test-{Guid.NewGuid():N}";
        Directory.CreateDirectory(CacheDir);
        string exePath = Path.Combine(CacheDir, InstallerFileName(version));
        string sha256Path = exePath + ".sha256";

        try
        {
            byte[] content = [.. "fake installer bytes, sha256sum-style sidecar"u8];
            await File.WriteAllBytesAsync(exePath, content);
            string hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
            // sha256sum's own output format is "HASH  filename" (lowercase hex).
            await File.WriteAllTextAsync(sha256Path, $"{hash}  {InstallerFileName(version)}");

            InstallerUpdater updater = new(new ServerConnection(), CacheDir);

            bool result = await updater.VerifyInstallerAsync(version);

            result.Should().BeTrue();
        }
        finally
        {
            File.Delete(exePath);
            File.Delete(sha256Path);
        }
    }

    [Fact]
    public async Task VerifyInstallerAsync_MismatchedSha256_ThrowsInvalidDataException()
    {
        string version = $"test-{Guid.NewGuid():N}";
        Directory.CreateDirectory(CacheDir);
        string exePath = Path.Combine(CacheDir, InstallerFileName(version));
        string sha256Path = exePath + ".sha256";

        try
        {
            await File.WriteAllBytesAsync(exePath, [.. "real content"u8]);
            // Sidecar records the hash of totally different bytes.
            string wrongHash = Convert.ToHexString(
                SHA256.HashData("different content"u8.ToArray())
            );
            await File.WriteAllTextAsync(sha256Path, wrongHash);

            InstallerUpdater updater = new(new ServerConnection(), CacheDir);

            Func<Task> act = () => updater.VerifyInstallerAsync(version);

            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally
        {
            File.Delete(exePath);
            File.Delete(sha256Path);
        }
    }

    [Fact]
    public async Task CleanCacheAsync_RemovesFilesForOtherVersions_KeepsCurrentAndPending()
    {
        string current = $"cur-{Guid.NewGuid():N}";
        string pending = $"pend-{Guid.NewGuid():N}";
        string stale = $"stale-{Guid.NewGuid():N}";
        Directory.CreateDirectory(CacheDir);

        string currentPath = Path.Combine(CacheDir, InstallerFileName(current));
        string pendingPath = Path.Combine(CacheDir, InstallerFileName(pending));
        string stalePath = Path.Combine(CacheDir, InstallerFileName(stale));

        try
        {
            await File.WriteAllTextAsync(currentPath, "current");
            await File.WriteAllTextAsync(pendingPath, "pending");
            await File.WriteAllTextAsync(stalePath, "stale");

            InstallerUpdater updater = new(new ServerConnection(), CacheDir);

            await updater.CleanCacheAsync(current, pending);

            File.Exists(currentPath)
                .Should()
                .BeTrue("the running version's installer must survive a prune");
            File.Exists(pendingPath)
                .Should()
                .BeTrue("the pending update's installer must survive a prune");
            File.Exists(stalePath)
                .Should()
                .BeFalse("an installer for neither the current nor pending version is stale");
        }
        finally
        {
            File.Delete(currentPath);
            File.Delete(pendingPath);
            if (File.Exists(stalePath))
                File.Delete(stalePath);
        }
    }

    [Fact]
    public async Task CleanCacheAsync_NoCacheDirectory_DoesNotThrow()
    {
        string cacheDir = Path.Combine(
            AppContext.BaseDirectory,
            $"missing-installer-cache-{Guid.NewGuid():N}"
        );
        InstallerUpdater updater = new(new ServerConnection(), cacheDir);

        Func<Task> act = () => updater.CleanCacheAsync("missing", null);

        await act.Should().NotThrowAsync();
        Directory.Exists(cacheDir).Should().BeFalse();
    }
}
