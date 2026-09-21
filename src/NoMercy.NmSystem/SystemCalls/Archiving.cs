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

using System.IO.Compression;
using NoMercy.NmSystem.FileSystem;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.NmSystem.SystemCalls;

public static class Archiving
{
    public static async Task<List<string>> ExtractArchive(
        IStorage storage,
        string filePath,
        string destination
    )
    {
        await EnsureExtractableAsync(storage, filePath);

        List<string> extractedFiles;

        if (filePath.EndsWith(".zip"))
        {
            extractedFiles = ExtractZipFile(storage, filePath, destination);
        }
        else if (
            filePath.EndsWith(".tar.xz")
            || filePath.EndsWith(".tar.gz")
            || filePath.EndsWith(".tgz")
        )
        {
            extractedFiles = await ExtractTarFile(storage, filePath, destination);
        }
        else
        {
            Logger.System($"Unsupported archive format for {filePath}", LogEventLevel.Error);
            return [];
        }

        foreach (string extractedFile in extractedFiles)
            await FilePermissions.SetExecutionPermissions(extractedFile);

        return extractedFiles;
    }

    /// <summary>
    /// Final gate immediately before shelling out to the extractor. Re-confirms the
    /// archive is actually present and non-empty — a defense against any state change
    /// between a caller's "download verified" step and this call (a concurrent
    /// re-provision attempt, external quarantine, or a network-mounted data volume
    /// where a just-written file briefly lags before it is visible to the child <c>tar</c>
    /// process). Retries with a short backoff to absorb that lag before giving up.
    /// </summary>
    /// <exception cref="FileNotFoundException">
    /// The archive is still missing or empty after every retry — extraction never runs
    /// against a file that was not actually there.
    /// </exception>
    private static async Task EnsureExtractableAsync(IStorage storage, string filePath)
    {
        const int maxAttempts = 3;
        TimeSpan delay = TimeSpan.FromMilliseconds(250);

        for (int attempt = 1; attempt <= maxAttempts; attempt++)
        {
            bool exists = storage.Exists(filePath);
            long size = exists ? storage.SizeOrZero(filePath) : 0;

            if (ArchiveExtractGate.CanProceed(exists, size))
                return;

            if (attempt == maxAttempts)
            {
                Logger.System(
                    $"Refusing to extract {filePath}: archive missing or empty "
                        + $"(exists={exists}, size={size}) after {maxAttempts} checks",
                    LogEventLevel.Error
                );
                throw new FileNotFoundException(
                    $"Cannot extract archive: file missing or empty at {filePath} — "
                        + "the download did not complete or was removed before extraction. "
                        + "Will retry the full download on the next provisioning attempt.",
                    filePath
                );
            }

            await Task.Delay(delay);
            delay *= 2;
        }
    }

    private static List<string> ExtractZipFile(
        IStorage storage,
        string zipFilePath,
        string extractToDirectory
    )
    {
        List<string> extractedFiles = [];
        string destinationRoot = Path.GetFullPath(extractToDirectory);

        try
        {
            using ZipArchive archive = ZipFile.OpenRead(zipFilePath);
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destinationPath = Path.Combine(
                    extractToDirectory,
                    NormalizeEntrySeparators(entry.FullName)
                );

                if (!IsPathContained(destinationRoot, destinationPath))
                {
                    Logger.System(
                        $"Rejected zip-slip entry '{entry.FullName}' in {zipFilePath}: resolves outside {destinationRoot}",
                        LogEventLevel.Error
                    );
                    throw new InvalidDataException(
                        $"Archive entry '{entry.FullName}' escapes the extraction root."
                    );
                }

                string destinationDir =
                    Path.GetDirectoryName(destinationPath) ?? extractToDirectory;

                if (!storage.Exists(destinationDir))
                    storage.CreateDirectory(destinationDir);

                if (string.IsNullOrEmpty(entry.Name)) // Skip directories
                    continue;

                entry.ExtractToFile(destinationPath, true);

                extractedFiles.Add(destinationPath);
            }
        }
        catch (Exception ex)
        {
            Logger.System(
                $"Failed to extract zip file {zipFilePath}: {ex.Message}",
                LogEventLevel.Error
            );
            throw new($"Failed to extract zip file {zipFilePath}", ex);
        }

        return extractedFiles;
    }

    private static async Task<List<string>> ExtractTarFile(
        IStorage storage,
        string tarFilePath,
        string extractToDirectory
    )
    {
        List<string> extractedFiles = [];
        string destinationRoot = Path.GetFullPath(extractToDirectory);

        try
        {
            // tar's -C target is not auto-created; on a fresh install the output
            // folder is absent and tar aborts ("Cannot open: No such file or
            // directory"), stranding the Binaries boot stage and every queue.
            if (!storage.Exists(extractToDirectory))
                storage.CreateDirectory(extractToDirectory);

            // List entries first so a traversal attempt can be rejected before
            // any file is written — the tar CLI has no per-entry containment guard.
            Shell.ExecResult listResult = await Shell.ExecAsync("tar", ["tf", tarFilePath]);

            if (listResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"Failed to list tar file {tarFilePath}: {listResult.StandardError}"
                );
            }

            string[] entries = listResult.StandardOutput.Split(
                '\n',
                StringSplitOptions.RemoveEmptyEntries
            );

            List<string> destinationPaths = [];
            foreach (string line in entries)
            {
                string entryName = line.Trim();
                string destinationPath = Path.Combine(
                    extractToDirectory,
                    NormalizeEntrySeparators(entryName)
                );

                if (!IsPathContained(destinationRoot, destinationPath))
                {
                    Logger.System(
                        $"Rejected zip-slip entry '{entryName}' in {tarFilePath}: resolves outside {destinationRoot}",
                        LogEventLevel.Error
                    );
                    throw new InvalidDataException(
                        $"Archive entry '{entryName}' escapes the extraction root."
                    );
                }

                destinationPaths.Add(destinationPath);
            }

            Shell.ExecResult extractResult = await Shell.ExecAsync(
                "tar",
                ["xf", tarFilePath, "-C", extractToDirectory]
            );

            if (extractResult.ExitCode != 0)
            {
                throw new InvalidOperationException(
                    $"tar exited with code {extractResult.ExitCode} extracting {tarFilePath}: {extractResult.StandardError}"
                );
            }

            foreach (string destinationPath in destinationPaths)
                if (storage.Exists(destinationPath))
                    extractedFiles.Add(destinationPath);
        }
        catch (Exception ex)
        {
            Logger.System(
                $"Failed to extract tar file {tarFilePath}: {ex.Message}",
                LogEventLevel.Error
            );
            throw new($"Failed to extract tar file {tarFilePath}", ex);
        }

        return extractedFiles;
    }

    /// <summary>
    /// Normalizes an archive entry's separators to '/' before it is ever
    /// combined with the extraction root. An archive is untrusted input that
    /// may target any extractor, on any OS, so an entry name can carry
    /// backslash-separated traversal (<c>..\evil.txt</c>) regardless of which
    /// platform built or is running the archive. On Windows backslash is
    /// already a separator, so this is a no-op there; on Linux/macOS
    /// backslash is just another filename character, so without this
    /// normalization <c>Path.Combine</c>/<c>Path.GetFullPath</c> would treat
    /// the whole traversal string as one literal (harmless-looking) file
    /// name instead of resolving the ".." segment it actually encodes.
    /// </summary>
    private static string NormalizeEntrySeparators(string entryName) =>
        entryName.Replace('\\', '/');

    /// <summary>
    /// Resolves both paths to their canonical full form and rejects any
    /// candidate that does not fall inside the destination root — the
    /// zip-slip / tar-slip guard against archive entries such as
    /// <c>../../evil</c> or an absolute path.
    /// </summary>
    private static bool IsPathContained(string destinationRoot, string candidatePath)
    {
        string normalizedRoot = destinationRoot.TrimEnd([
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar,
        ]);
        string fullCandidatePath = Path.GetFullPath(candidatePath);

        return fullCandidatePath == normalizedRoot
            || fullCandidatePath.StartsWith(
                normalizedRoot + Path.DirectorySeparatorChar,
                StringComparison.Ordinal
            );
    }
}
