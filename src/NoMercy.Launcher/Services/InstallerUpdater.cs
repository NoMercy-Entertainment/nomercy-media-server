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

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using NoMercy.Launcher.Models;

namespace NoMercy.Launcher.Services;

/// <summary>
/// Handles installer-based auto-update on Windows.
/// Mirrors Docker Desktop's pattern: stable cache dir, SHA-256 verify, prune stale installers.
/// </summary>
public class InstallerUpdater(ServerConnection serverConnection)
{
    private static readonly HttpClient HttpClient = new();
    private readonly HttpClient _httpClient = HttpClient;
    private readonly string _cacheDir = CacheDir;

    // %LocalAppData%\NoMercy\UpdateCache\
    private static string CacheDir =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "NoMercy",
            "UpdateCache"
        );

    static InstallerUpdater()
    {
        HttpClient.DefaultRequestHeaders.Add("User-Agent", "NoMercyLauncher/1.0");
    }

    internal InstallerUpdater(
        ServerConnection serverConnection,
        HttpClient httpClient,
        string cacheDir
    )
        : this(serverConnection)
    {
        _httpClient = httpClient;
        _cacheDir = cacheDir;
    }

    internal InstallerUpdater(ServerConnection serverConnection, string cacheDir)
        : this(serverConnection)
    {
        _cacheDir = cacheDir;
    }

    /// <summary>
    /// True when running from an installer deployment (NOMERCY_INSTALL_DIR env is set,
    /// or the launcher lives outside the binaries path).
    /// </summary>
    public Task<bool> IsInstallerDeploymentAsync()
    {
        string? installDir = Environment.GetEnvironmentVariable("NOMERCY_INSTALL_DIR");
        if (!string.IsNullOrEmpty(installDir))
            return Task.FromResult(true);

        string? ownDir = Path.GetDirectoryName(
            Environment.ProcessPath ?? Assembly.GetExecutingAssembly().Location
        );

        if (ownDir is null)
            return Task.FromResult(false);

        string binariesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "NoMercy",
            "binaries"
        );

        bool isInBinaries = string.Equals(
            Path.GetFullPath(ownDir),
            Path.GetFullPath(binariesPath),
            StringComparison.OrdinalIgnoreCase
        );

        return Task.FromResult(!isInBinaries);
    }

    /// <summary>
    /// GET /manage/activity — returns stream and encode counts from the running server.
    /// </summary>
    public async Task<ActivityInfo?> GetActivityAsync(CancellationToken ct = default)
    {
        return await serverConnection.GetAsync<ActivityInfo>("/manage/activity", ct);
    }

    /// <summary>
    /// Downloads the installer to the stable cache dir.
    /// Skips download if the file already exists and its SHA-256 matches the sibling .sha256 asset.
    /// Returns true when the installer is ready to launch.
    /// </summary>
    public async Task<bool> DownloadInstallerAsync(
        string version,
        IProgress<double>? progress = null,
        CancellationToken ct = default
    )
    {
        Directory.CreateDirectory(_cacheDir);

        string fileName = $"NoMercyMediaServer-{version}-windows-x64-setup.exe";
        string destPath = Path.Combine(_cacheDir, fileName);
        string sha256Path = destPath + ".sha256";

        // Check for valid cached copy first
        if (File.Exists(destPath) && File.Exists(sha256Path))
        {
            LauncherLog.Info($"Installer already cached at {destPath}, verifying SHA-256...");
            bool cacheValid;
            try
            {
                cacheValid = await VerifyInstallerAsync(version, ct);
            }
            catch (InvalidDataException)
            {
                cacheValid = false;
            }

            if (cacheValid)
            {
                LauncherLog.Info("Cached installer SHA-256 matches — skipping download");
                return true;
            }

            LauncherLog.Info("SHA-256 mismatch on cached installer — re-downloading");
            File.Delete(destPath);
            File.Delete(sha256Path);
        }

        string tag = $"v{version}";
        string baseUrl =
            $"https://github.com/NoMercy-Entertainment/nomercy-media-server/releases/download/{tag}";
        string installerUrl = $"{baseUrl}/{fileName}";
        string sha256Url = $"{baseUrl}/{fileName}.sha256";

        // Download SHA-256 sidecar first (best-effort)
        try
        {
            using HttpResponseMessage sha256Response = await _httpClient.GetAsync(
                sha256Url,
                HttpCompletionOption.ResponseHeadersRead,
                ct
            );

            if (sha256Response.IsSuccessStatusCode)
            {
                string sha256Content = await sha256Response.Content.ReadAsStringAsync(ct);
                await File.WriteAllTextAsync(sha256Path, sha256Content, ct);
                LauncherLog.Info("Downloaded SHA-256 sidecar");
            }
            else
            {
                LauncherLog.Info(
                    $"SHA-256 sidecar not found for {version} — will continue without verification"
                );
            }
        }
        catch (Exception ex)
        {
            LauncherLog.Info($"Could not fetch SHA-256 sidecar: {ex.Message}");
        }

        // Download installer
        LauncherLog.Info($"Downloading installer from {installerUrl}");

        using HttpResponseMessage response = await _httpClient.GetAsync(
            installerUrl,
            HttpCompletionOption.ResponseHeadersRead,
            ct
        );

        response.EnsureSuccessStatusCode();

        long? total = response.Content.Headers.ContentLength;

        await using FileStream fileStream = new(
            destPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            81920,
            true
        );

        await using Stream download = await response.Content.ReadAsStreamAsync(ct);

        byte[] buffer = new byte[81920];
        long bytesRead = 0;
        int read;

        while ((read = await download.ReadAsync(buffer, ct)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), ct);
            bytesRead += read;

            if (progress is not null && total is > 0)
                progress.Report((double)bytesRead / total.Value);
        }

        LauncherLog.Info($"Installer downloaded to {destPath} ({bytesRead} bytes)");
        return true;
    }

    /// <summary>
    /// Verifies the cached installer against its .sha256 sidecar.
    /// Returns true when the file matches or when no sidecar exists (legacy release).
    /// Throws <see cref="InvalidDataException"/> on mismatch.
    /// </summary>
    public async Task<bool> VerifyInstallerAsync(string version, CancellationToken ct = default)
    {
        string fileName = $"NoMercyMediaServer-{version}-windows-x64-setup.exe";
        string destPath = Path.Combine(_cacheDir, fileName);
        string sha256Path = destPath + ".sha256";

        if (!File.Exists(sha256Path))
        {
            LauncherLog.Info($"No SHA-256 sidecar for {version} — skipping verification");
            return true;
        }

        string expectedLine = (await File.ReadAllTextAsync(sha256Path, ct)).Trim();

        // Accept both bare-hash and "HASH  filename" formats
        string expected = expectedLine.Split(' ', 2)[0].ToUpperInvariant();

        using SHA256 sha256 = SHA256.Create();
        await using FileStream fs = File.OpenRead(destPath);
        byte[] hash = await sha256.ComputeHashAsync(fs, ct);
        string actual = Convert.ToHexString(hash).ToUpperInvariant();

        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            LauncherLog.Error($"SHA-256 mismatch for {version}: expected {expected}, got {actual}");
            throw new InvalidDataException(
                $"Installer SHA-256 mismatch — file may be corrupted. Expected {expected[..8]}..., got {actual[..8]}..."
            );
        }

        LauncherLog.Info($"SHA-256 verified for {version}");
        return true;
    }

    /// <summary>
    /// Graceful orderly shutdown: stop App via IPC, stop Server via IPC.
    /// The installer handles any remaining processes via CLOSEAPPLICATIONS.
    /// </summary>
    public async Task ShutdownAllAsync(
        ServerConnection connection,
        ServerProcessLauncher processLauncher,
        CancellationToken ct = default
    )
    {
        // Stop the App process first
        try
        {
            await connection.PostAsync("/manage/app/stop", ct);
            LauncherLog.Info("App stop command sent");
        }
        catch (Exception ex)
        {
            LauncherLog.Info($"App stop via IPC skipped: {ex.Message}");
        }

        await Task.Delay(500, ct);

        // Stop the Server
        try
        {
            await connection.PostAsync("/manage/stop", ct);
            LauncherLog.Info("Server stop command sent");
        }
        catch (Exception ex)
        {
            LauncherLog.Info($"Server stop via IPC skipped: {ex.Message}");
        }

        LauncherLog.Info("Waiting for server process to exit (30s timeout)");
        bool exited = await processLauncher.WaitForServerExitAsync(TimeSpan.FromSeconds(30));

        if (!exited)
        {
            LauncherLog.Info("Server did not exit gracefully within 30s — continuing anyway");
        }
    }

    /// <summary>
    /// Spawns the installer in silent mode with the appropriate flags.
    /// Passes /UpdateCacheCleanup=1 so the ISS [Code] section can delete the installer file post-install.
    /// Passes /LaunchAfterInstall=1 or 0 based on the user's auto-start preference.
    /// Then exits the launcher so the installer can apply CLOSEAPPLICATIONS cleanly.
    /// </summary>
    public Task LaunchInstallerAsync(string version, bool launcherAutoStart)
    {
        string fileName = $"NoMercyMediaServer-{version}-windows-x64-setup.exe";
        string installerPath = Path.Combine(_cacheDir, fileName);

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("Installer not found in cache", installerPath);

        string launchAfter = launcherAutoStart ? "1" : "0";

        ProcessStartInfo startInfo = new(installerPath)
        {
            UseShellExecute = true, // needed for UAC elevation
            Arguments =
                $"/SILENT /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS /UpdateCacheCleanup=1 /LaunchAfterInstall={launchAfter}",
        };

        LauncherLog.Info($"Launching installer: {installerPath} {startInfo.Arguments}");

        Process.Start(startInfo);

        // Exit the launcher — the installer handles the rest via CLOSEAPPLICATIONS
        Environment.Exit(0);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Prunes stale installers from the cache.
    /// Keeps the installer for the current running version and the pending version; deletes all others.
    /// </summary>
    public Task CleanCacheAsync(
        string currentVersion,
        string? pendingVersion,
        CancellationToken ct = default
    )
    {
        if (!Directory.Exists(_cacheDir))
            return Task.CompletedTask;

        HashSet<string> keep =
        [
            $"NoMercyMediaServer-{currentVersion}-windows-x64-setup.exe",
            $"NoMercyMediaServer-{currentVersion}-windows-x64-setup.exe.sha256",
        ];

        if (pendingVersion is not null)
        {
            keep.Add($"NoMercyMediaServer-{pendingVersion}-windows-x64-setup.exe");
            keep.Add($"NoMercyMediaServer-{pendingVersion}-windows-x64-setup.exe.sha256");
        }

        foreach (string file in Directory.EnumerateFiles(_cacheDir, "NoMercyMediaServer-*-setup*"))
        {
            string name = Path.GetFileName(file);
            if (keep.Contains(name))
                continue;

            try
            {
                File.Delete(file);
                LauncherLog.Info($"Pruned stale installer cache entry: {name}");
            }
            catch (Exception ex)
            {
                LauncherLog.Info($"Could not prune {name}: {ex.Message}");
            }
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Full update orchestration for installer deployments.
    /// </summary>
    public async Task<bool> DoUpdateAsync(
        string version,
        bool launcherAutoStart,
        ServerConnection connection,
        ServerProcessLauncher processLauncher,
        IProgress<double>? progress = null,
        CancellationToken ct = default
    )
    {
        // Background cache prune (fire-and-forget)
        _ = Task.Run(() => CleanCacheAsync(string.Empty, version, ct), ct);

        bool downloaded = await DownloadInstallerAsync(version, progress, ct);
        if (!downloaded)
            return false;

        await VerifyInstallerAsync(version, ct);

        await ShutdownAllAsync(connection, processLauncher, ct);

        await LaunchInstallerAsync(version, launcherAutoStart);

        return true;
    }
}
