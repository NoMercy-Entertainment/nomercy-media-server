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
using Newtonsoft.Json;
using NoMercy.NmSystem.Information;
using NoMercy.Setup.Dto;
using NoMercy.Setup.Server;
using NoMercy.Storage.Drivers.Local;
using NoMercy.Storage.Validation;
using NoMercy.Tests.Setup.Infrastructure;

namespace NoMercy.Tests.Setup;

/// <summary>
/// Characterizes the <see cref="Binaries.DownloadServerUpdate"/> decision matrix —
/// the logic that decides whether a new server binary is fetched and staged for a
/// self-hosted user. A regression here is high blast radius: it either strands users
/// on an old build, re-downloads the same binary on every check (the "stuck update
/// loop"), clobbers an installer deployment, or — worst — stages a downgrade. Each
/// <see cref="ServerUpdateResult"/> branch that can be reached without a real
/// version-stamped PE on disk is locked here.
/// </summary>
/// <remarks>
/// Testability: <see cref="Binaries"/> exposes an internal HttpClient constructor and
/// <see cref="Software.Version"/> is a settable static, so the running version and the
/// "latest" release are both controllable without the network. <c>NOMERCY_APP_PATH</c>
/// is isolated to a temp directory per the <c>DegradedModeStartupTests</c> pattern so
/// the release-metadata cache and any staged binary land in the temp tree — never the
/// developer's real app-data path, where a fabricated "latest" cache could otherwise
/// trigger a bogus real update.
///
/// The <see cref="ServerUpdateResult.RestartNeeded"/> branch is intentionally not
/// covered: it fires only when a binary already on disk reports a matching file
/// version, which requires a genuine version-stamped PE that a unit test cannot
/// synthesize (a plain file reads back as 0.0.0 → null). The download-and-stage
/// mechanics themselves are covered by <c>BinaryDownloaderTests</c>.
/// </remarks>
[Trait("Category", "Unit")]
[Collection(ProcessWideSetupStateCollection.Name)]
public sealed class ServerUpdateDecisionTests : IDisposable
{
    private readonly Version? _originalVersion;
    private readonly string? _originalAppPath;
    private readonly string? _originalInstallDir;
    private readonly string _tempAppPath;

    public ServerUpdateDecisionTests()
    {
        _originalVersion = Software.Version;
        _originalAppPath = Environment.GetEnvironmentVariable("NOMERCY_APP_PATH");
        _originalInstallDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");

        _tempAppPath = Path.Combine(Path.GetTempPath(), $"nomercy-update-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _tempAppPath);

        // Installer deployments are opt-in via this variable; clear it so the default
        // test is a standalone deployment. Individual tests set it explicitly.
        Environment.SetEnvironmentVariable("NOMERCY_INSTALL_DIR", null);
    }

    public void Dispose()
    {
        Software.Version = _originalVersion;
        Environment.SetEnvironmentVariable("NOMERCY_APP_PATH", _originalAppPath);
        Environment.SetEnvironmentVariable("NOMERCY_INSTALL_DIR", _originalInstallDir);

        // Best-effort: the logger keeps its daily log file under the isolated app
        // path open for the process lifetime, so a recursive delete races that handle.
        // The temp tree is disposable either way — the OS temp sweep reclaims it.
        try
        {
            if (Directory.Exists(_tempAppPath))
                Directory.Delete(_tempAppPath, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task DownloadServerUpdate_NoAssetsPublished_ReturnsNoAssetFound()
    {
        Software.Version = new(1, 0, 0);
        Binaries binaries = BuildBinaries(WithReleaseThen("v9.9.9", assets: []));

        ServerUpdateResult result = await binaries.DownloadServerUpdate();

        result.Should().Be(ServerUpdateResult.NoAssetFound);
    }

    [Fact]
    public async Task DownloadServerUpdate_LatestEqualsRunning_ReturnsAlreadyUpToDate()
    {
        // Idempotency: a check when already on the latest version must never download.
        Software.Version = new(2, 0, 1);
        Binaries binaries = BuildBinaries(WithReleaseThen("v2.0.1", ServerAssets()));

        ServerUpdateResult result = await binaries.DownloadServerUpdate();

        result.Should().Be(ServerUpdateResult.AlreadyUpToDate);
        StagedBinaryExists().Should().BeFalse("an up-to-date server must not stage a binary");
    }

    [Fact]
    public async Task DownloadServerUpdate_LatestOlderThanRunning_ReturnsAlreadyUpToDate()
    {
        // Never downgrade: a running build ahead of GitHub's "latest" (e.g. a user on a
        // hotfix) must hold, not roll back to the older published release.
        Software.Version = new(9, 9, 9);
        Binaries binaries = BuildBinaries(WithReleaseThen("v1.0.0", ServerAssets()));

        ServerUpdateResult result = await binaries.DownloadServerUpdate();

        result.Should().Be(ServerUpdateResult.AlreadyUpToDate);
        StagedBinaryExists().Should().BeFalse("a downgrade must never be staged");
    }

    [Fact]
    public async Task DownloadServerUpdate_InstallerDeployment_DefersToInstaller()
    {
        // An installer deployment owns its own update flow; the server must not fetch a
        // binary into the binaries path behind the installer's back.
        Software.Version = new(1, 0, 0);
        Environment.SetEnvironmentVariable("NOMERCY_INSTALL_DIR", _tempAppPath);
        Binaries binaries = BuildBinaries(WithReleaseThen("v9.9.9", ServerAssets()));

        ServerUpdateResult result = await binaries.DownloadServerUpdate();

        result.Should().Be(ServerUpdateResult.UseInstaller);
        StagedBinaryExists().Should().BeFalse("an installer deployment must not stage a binary");
    }

    [Fact]
    public async Task DownloadServerUpdate_NewerReleaseAvailable_DownloadsAndStages()
    {
        // The happy path: a genuinely newer release on a standalone deployment is fetched,
        // integrity-checked against the GitHub asset digest, and staged for the next restart.
        Software.Version = new(1, 0, 0);
        byte[] payload = [.. "newer server binary"u8];
        string digest =
            "sha256:" + Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();

        SequencedHttpHandler handler = new();
        handler.Enqueue(() => Json(BuildRelease("v9.9.9", ServerAssets(digest))));
        handler.Enqueue(() =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) }
        );

        Binaries binaries = BuildBinaries(handler);

        ServerUpdateResult result = await binaries.DownloadServerUpdate();

        result.Should().Be(ServerUpdateResult.Downloaded);
        StagedBinaryExists().Should().BeTrue("a newer release must be staged for restart");
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool StagedBinaryExists() =>
        File.Exists(AppFiles.ServerTempExePath)
        && new FileInfo(AppFiles.ServerTempExePath).Length > 0;

    private static SequencedHttpHandler WithReleaseThen(string tagName, Asset[] assets)
    {
        SequencedHttpHandler handler = new();
        handler.Enqueue(() => Json(BuildRelease(tagName, assets)));
        return handler;
    }

    private static HttpResponseMessage Json(GithubReleaseResponse release) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonConvert.SerializeObject(release)),
        };

    private static GithubReleaseResponse BuildRelease(string tagName, Asset[] assets) =>
        new()
        {
            TagName = tagName,
            PublishedAt = DateTimeOffset.UtcNow.AddDays(-1),
            Assets = assets,
        };

    // The full platform asset set so the release resolves on whichever OS the suite
    // runs on (Windows locally, Linux in CI coverage). Names mirror the production
    // switch in Binaries.DownloadServerUpdate.
    private static Asset[] ServerAssets(string digest = "") =>
        [
            ServerAsset("NoMercyMediaServer-windows-x64.exe", digest),
            ServerAsset("NoMercyMediaServer-linux-x64", digest),
            ServerAsset("NoMercyMediaServer-linux-arm64", digest),
            ServerAsset("NoMercyMediaServer-macos-x64", digest),
        ];

    private static Asset ServerAsset(string name, string digest) =>
        new()
        {
            Name = name,
            BrowserDownloadUrl = new($"https://example.com/{name}"),
            Size = 1,
            Digest = digest,
        };

    private static Binaries BuildBinaries(SequencedHttpHandler handler)
    {
        LocalStorageDriver driver = new();
        StoragePathGuard guard = new([], driver);
        LocalStorage storage = new(driver, guard);
        HttpClient http = new(handler);
        return new(driver, storage, http);
    }
}
