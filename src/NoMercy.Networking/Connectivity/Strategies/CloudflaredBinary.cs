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
using Microsoft.Extensions.Logging;
using NoMercy.NmSystem.Information;
using NoMercy.Storage;

namespace NoMercy.Networking.Connectivity.Strategies;

/// <summary>
/// What both tunnel strategies share about the cloudflared executable: whether it is on
/// disk, which file is on disk, and which stray processes of it are this server's to stop.
/// </summary>
internal static class CloudflaredBinary
{
    private const string ProcessNamePrefix = "cloudflared";

    public static bool Exists(IStorageDriver driver) => driver.FileExists(AppFiles.CloudflareDPath);

    /// <summary>
    /// Identifies the file currently at the binary path — write time and size, which an
    /// upgrade always changes — or null while it is missing or mid-replacement. A strategy
    /// keeps the stamp it started from and compares it later to notice a replaced binary.
    /// </summary>
    public static string? Stamp(IStorageDriver driver)
    {
        string path = AppFiles.CloudflareDPath;
        try
        {
            if (!driver.FileExists(path))
                return null;

            return $"{driver.GetLastWriteTimeUtc(path).Ticks}:{driver.GetFileSize(path)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Kills every cloudflared running from this server's dependencies directory other than
    /// <paramref name="owned"/>. A crash, a container kill or a debugger stop skips
    /// TeardownAsync and leaves the child running; a named tunnel then registers a second
    /// connector on every restart, and a quick tunnel keeps a dead server's name alive.
    /// Matching on the directory rather than the exact path also catches a process whose
    /// image an upgrade has since renamed to <c>.bak</c>, and keeps a cloudflared the user
    /// runs for their own tunnels out of scope.
    /// </summary>
    public static void ReapOrphans(ILogger logger, Process? owned)
    {
        string ourDirectory;
        try
        {
            ourDirectory = Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(AppFiles.DependenciesPath)
            );
        }
        catch (Exception)
        {
            return;
        }

        int? ownedId = IdOf(owned);

        foreach (Process candidate in Process.GetProcesses())
            using (candidate)
            {
                try
                {
                    if (candidate.Id == ownedId)
                        continue;

                    if (
                        !candidate.ProcessName.StartsWith(
                            ProcessNamePrefix,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    string? path = candidate.MainModule?.FileName;
                    string? directory = path is null
                        ? null
                        : Path.GetDirectoryName(Path.GetFullPath(path));
                    if (
                        directory is null
                        || !string.Equals(
                            directory,
                            ourDirectory,
                            StringComparison.OrdinalIgnoreCase
                        )
                    )
                        continue;

                    logger.LogWarning(
                        "Found a cloudflared left over from a previous run (pid {Pid}) — stopping it so this server does not run two tunnels",
                        candidate.Id
                    );

                    candidate.Kill(true);
                    candidate.WaitForExit(5000);
                }
                catch (Exception ex)
                {
                    // ProcessName and MainModule throw for processes we cannot inspect or
                    // that exited meanwhile; neither is ours to reap.
                    logger.LogDebug("Skipped cloudflared pid inspection: {Message}", ex.Message);
                }
            }
    }

    /// <summary>
    /// The id of a started process, or null for none or one whose Start() threw — asking
    /// that one for its id throws too.
    /// </summary>
    private static int? IdOf(Process? process)
    {
        try
        {
            return process?.Id;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}
