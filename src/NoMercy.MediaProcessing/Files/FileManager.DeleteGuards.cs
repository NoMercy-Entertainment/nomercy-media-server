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

using System.Collections.Concurrent;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Extensions;
using NoMercy.Storage;
using Serilog.Events;
using Logger = NoMercy.NmSystem.SystemCalls.Logger;

namespace NoMercy.MediaProcessing.Files;

public partial class FileManager
{
    private const double MassMissingShare = 0.20;

    // One missing title is a deletion, never an outage, however small the library.
    private const int MassMissingMinimumTitles = 2;

    // Each title of a library scan runs in its own FileManager, so the verdict is shared.
    private static readonly TimeSpan MassMissingVerdictLifetime = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<Ulid, MassMissingVerdict> MassMissingVerdicts =
        new();
    private static readonly SemaphoreSlim MassMissingGate = new(1, 1);

    private sealed record MassMissingVerdict(bool SkipDeletes, DateTime CheckedAtUtc);

    /// <summary>Why a title that resolved nothing keeps its rows, or null when it is gone.</summary>
    private async Task<string?> ReasonToKeepRecords(Library library)
    {
        string? siblingCopy = FindSiblingCopyOfTitleFolder();
        if (siblingCopy is not null)
            return siblingCopy;

        return await LibraryIsMassMissing(library)
            ? $"too many titles of library '{library.Title}' ({library.Id}) are missing at once"
            : null;
    }

    /// <summary>Names a folder in a library root called like the stored title folder plus a suffix.</summary>
    private string? FindSiblingCopyOfTitleFolder()
    {
        string? storedName = (Movie?.Folder ?? Show?.Folder)?.Replace("/", "");
        if (string.IsNullOrEmpty(storedName))
            return null;

        foreach (Folder rootFolder in LibraryRootFolders)
        {
            IStorage storage = StorageFor(rootFolder);
            try
            {
                string root = ResolveBackendPath(storage, rootFolder.Path);
                if (!storage.Driver.DirectoryExists(root))
                    continue;

                foreach (
                    string entry in storage.Driver.EnumerateFileSystemEntries(
                        root,
                        "*",
                        SearchOption.TopDirectoryOnly
                    )
                )
                {
                    string name = Path.GetFileName(entry).OrEmpty();
                    if (IsSuffixedCopyOf(name, storedName) && storage.Driver.DirectoryExists(entry))
                        return $"sibling copy of its folder found at '{entry}'";
                }
            }
            catch (Exception ex)
            {
                // A root that cannot be listed proves nothing about the title: keep the rows.
                return $"library root '{rootFolder.Path}' could not be listed ({ex.Message})";
            }
        }

        return null;
    }

    private static bool IsSuffixedCopyOf(string name, string storedName) =>
        name.Length > storedName.Length
        && name.StartsWith(storedName, StringComparison.OrdinalIgnoreCase)
        && !char.IsLetterOrDigit(name[storedName.Length]);

    /// <summary>
    /// Whether more than <see cref="MassMissingShare"/> of the library's registered titles
    /// have no readable path. Asked once per library per verdict lifetime.
    /// </summary>
    private async Task<bool> LibraryIsMassMissing(Library library)
    {
        await MassMissingGate.WaitAsync();
        try
        {
            if (
                MassMissingVerdicts.TryGetValue(library.Id, out MassMissingVerdict? cached)
                && DateTime.UtcNow - cached.CheckedAtUtc < MassMissingVerdictLifetime
            )
                return cached.SkipDeletes;

            Dictionary<int, List<RecordedVideoFileLocation>> recorded =
                await fileRepository.GetRecordedVideoFileLocationsByLibraryAsync(
                    library.Id,
                    library.Type
                );

            int missing = recorded.Values.Count(locations => !AnyRecordedPathReads(locations));
            bool skipDeletes =
                missing >= MassMissingMinimumTitles && missing > recorded.Count * MassMissingShare;

            if (skipDeletes)
                Logger.App(
                    $"[FindFiles] library '{library.Title}' ({library.Id}): {missing} of "
                        + $"{recorded.Count} titles have no readable registered media (more than "
                        + $"{MassMissingShare * 100:0}%) — skipping every removal in this library "
                        + "for this scan",
                    LogEventLevel.Error
                );

            MassMissingVerdicts[library.Id] = new(skipDeletes, DateTime.UtcNow);
            return skipDeletes;
        }
        finally
        {
            MassMissingGate.Release();
        }
    }
}
