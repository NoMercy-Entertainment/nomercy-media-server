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

using Microsoft.Extensions.Logging;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Storage;

namespace NoMercy.Data.Plugins;

/// <summary>
/// The files of one render on disk, from the first folder it makes to the
/// last name it gives away.
/// <para>
/// ffmpeg writes every output under a temporary name. Only a run that exited
/// 0 gets its real names, and it gets all of them or none: a render that
/// replaced two of its four files describes a mix of two runs, which no
/// caller can use and none would think to check for. Everything else - a
/// failure, a timeout, a cancellation - ends in <see cref="Cleanup" />, which
/// leaves the folder as the run found it.
/// </para>
/// <para>
/// The plugin's data folder is always a local folder, so this goes to the
/// storage driver with full paths instead of through a scoped storage.
/// </para>
/// </summary>
internal sealed class PluginAudioRenderFiles(
    Ulid pluginId,
    string dataFolderPath,
    IReadOnlyList<PluginAudioRenderTarget> targets,
    IStorageDriver driver,
    ILogger logger
)
{
    private const string PreviousSuffix = ".previous";

    private readonly string _root = Path.GetFullPath(dataFolderPath);

    // Deepest last, which is the order they were made in.
    private readonly List<string> _foldersThisRunMade = [];

    private bool _committed;

    /// <summary>What the files this run replaces were holding before it started.</summary>
    public long ReplacedBytes { get; private set; }

    /// <summary>
    /// Makes the folders the outputs land in, clears out what an interrupted
    /// run left in them, and measures what is about to be replaced.
    /// </summary>
    public void Prepare()
    {
        foreach (string folder in targets.Select(Folder).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            MakeFolder(folder);
            RemoveLeftovers(folder);
        }

        ReplacedBytes = targets.Sum(target => SizeOrZero(target.FinalPath));
    }

    /// <summary>
    /// How much more the plugin's folder would hold if the run stopped now
    /// and kept everything: what ffmpeg has written so far, less what it
    /// replaces. This is what the disk quota is asked about.
    /// </summary>
    public long NetBytes() => targets.Sum(target => SizeOrZero(target.TempPath)) - ReplacedBytes;

    /// <summary>
    /// The pad of the first output ffmpeg never wrote a file for, or null
    /// when every one is there. Exit code 0 is ffmpeg's word, not proof: a
    /// pad that produced no frames still exits clean.
    /// </summary>
    public string? PadWithoutAFile() =>
        targets.FirstOrDefault(target => !driver.FileExists(target.TempPath))?.Pad;

    /// <summary>
    /// Gives every output its real name, all of them or none.
    /// <para>
    /// The files being replaced are moved aside first. That is the step that
    /// can fail - a file the plugin itself still holds open cannot be moved
    /// on Windows - and it fails before a single new file has landed, so
    /// putting the old ones back restores the folder exactly.
    /// </para>
    /// </summary>
    public void Commit()
    {
        List<(string Aside, string Final)> movedAside = [];
        List<string> placed = [];

        try
        {
            foreach (PluginAudioRenderTarget target in targets)
            {
                if (!driver.FileExists(target.FinalPath))
                    continue;

                string aside = target.TempPath + PreviousSuffix;
                driver.MoveFile(target.FinalPath, aside);
                movedAside.Add((aside, target.FinalPath));
            }

            foreach (PluginAudioRenderTarget target in targets)
            {
                driver.MoveFile(target.TempPath, target.FinalPath);
                placed.Add(target.FinalPath);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            foreach (string final in placed)
                TryDelete(final);

            foreach ((string aside, string final) in movedAside)
                TryMove(aside, final);

            throw;
        }

        foreach ((string aside, string _) in movedAside)
            TryDelete(aside);

        _committed = true;
    }

    /// <summary>What a committed run left behind, in the order it was asked for.</summary>
    public IReadOnlyList<PluginAudioRenderedFile> Rendered() =>
        targets
            .Select(target => new PluginAudioRenderedFile(
                target.RelativePath,
                driver.GetFileSize(target.FinalPath)
            ))
            .ToList();

    /// <summary>
    /// Removes whatever the run did not give a real name: the temporary
    /// files, and after a run that kept nothing, the folders it made for
    /// them. Safe to call on every way out.
    /// </summary>
    public void Cleanup()
    {
        foreach (PluginAudioRenderTarget target in targets)
            TryDelete(target.TempPath);

        if (_committed)
            return;

        for (int index = _foldersThisRunMade.Count - 1; index >= 0; index--)
            TryRemoveEmptyFolder(_foldersThisRunMade[index]);
    }

    private static string Folder(PluginAudioRenderTarget target) =>
        Path.GetDirectoryName(target.FinalPath)!;

    /// <summary>
    /// Creates <paramref name="folder" /> and remembers each level that was
    /// not there, so a run that keeps nothing can take them away again.
    /// </summary>
    private void MakeFolder(string folder)
    {
        Stack<string> missing = new();

        for (
            string? current = folder;
            current is not null
                && current.Length > _root.Length
                && !driver.DirectoryExists(current);
            current = Path.GetDirectoryName(current)
        )
            missing.Push(current);

        if (missing.Count == 0)
            return;

        driver.CreateDirectory(folder);
        _foldersThisRunMade.AddRange(missing);
    }

    /// <summary>
    /// A server that stopped mid-render never reached <see cref="Cleanup" />.
    /// Only one run of a plugin exists at a time, so any file under the
    /// temporary prefix in this folder belongs to a run that is gone.
    /// </summary>
    private void RemoveLeftovers(string folder)
    {
        foreach (
            string leftover in driver
                .EnumerateFileSystemEntries(
                    folder,
                    PluginAudioRenderPlan.TempPrefix + "*",
                    SearchOption.TopDirectoryOnly
                )
                .ToList()
        )
            TryDelete(leftover);
    }

    private long SizeOrZero(string path)
    {
        try
        {
            return driver.FileExists(path) ? driver.GetFileSize(path) : 0;
        }
        catch (IOException)
        {
            // Gone between the two calls: nothing there to count.
            return 0;
        }
    }

    /// <summary>
    /// Best effort. A file that will not go is reported and left: failing a
    /// finished render over it would throw away the result to protect a
    /// leftover the next run removes anyway.
    /// </summary>
    private void TryDelete(string path)
    {
        try
        {
            if (driver.FileExists(path))
                driver.DeleteFile(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "plugin {PluginId}: could not remove a render file from the plugin's data folder",
                pluginId
            );
        }
    }

    private void TryMove(string from, string to)
    {
        try
        {
            if (driver.FileExists(from) && !driver.FileExists(to))
                driver.MoveFile(from, to);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "plugin {PluginId}: could not put back a file a failed render had moved aside",
                pluginId
            );
        }
    }

    private void TryRemoveEmptyFolder(string folder)
    {
        try
        {
            if (
                driver.DirectoryExists(folder)
                && !driver
                    .EnumerateFileSystemEntries(folder, "*", SearchOption.TopDirectoryOnly)
                    .Any()
            )
                driver.DeleteDirectory(folder, recursive: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(
                exception,
                "plugin {PluginId}: could not remove a folder a failed render had made",
                pluginId
            );
        }
    }
}
