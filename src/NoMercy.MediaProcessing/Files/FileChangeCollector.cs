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

using System.Text.RegularExpressions;
using NoMercy.Database.Models.Libraries;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;
using NoMercy.NmSystem.SystemCalls;
using NoMercy.Storage;
using Serilog.Events;

namespace NoMercy.MediaProcessing.Files;

public readonly record struct FileSignature(long Length, DateTime LastWriteUtc);

/// <summary>
/// Turns raw file events into one group per folder and publishes a group only when it is
/// safe: the event is not the server's own output or a temp file, and every created or
/// changed file kept the same size and time for a whole quiet period.
/// </summary>
public sealed partial class FileChangeCollector(
    Func<FileChangeGroup, Task> publish,
    ServerWriteLedger ledger,
    TimeProvider clock,
    TimeSpan quiet,
    Func<string, FileSignature?>? probe = null,
    Func<string?>? transcodeRoot = null
)
{
    private static readonly string[] TemporaryExtensions =
    [
        ".tmp",
        ".temp",
        ".part",
        ".partial",
        ".filepart",
        ".crdownload",
        ".download",
        ".nmpart",
        ".!qb",
    ];

    [GeneratedRegex(
        @"^(video_.*|audio_.*|subtitles|fonts|thumbs|metadata|scans|cds.*|NCED|NCOP)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex EncodingOutputRegex();

    private sealed class Pending(FileChangeGroup group, DateTimeOffset lastEvent)
    {
        public FileChangeGroup Group { get; } = group;
        public DateTimeOffset LastEvent { get; set; } = lastEvent;
        public Dictionary<string, FileSignature?> Signatures { get; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private readonly Dictionary<string, Pending> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _lock = new();
    private readonly Func<string, FileSignature?> _probe = probe ?? ProbeDisk;

    /// <summary>False for events the server must not act on: its own output and temp files.</summary>
    public bool Accepts(string fullPath, string? oldFullPath = null)
    {
        if (IsInEncodingOutputDirectory(fullPath))
            return false;

        if (IsTemporaryName(fullPath))
            return false;

        string? root = transcodeRoot?.Invoke();
        if (
            !string.IsNullOrWhiteSpace(root)
            && ServerWriteLedger
                .Normalize(fullPath)
                .StartsWith(
                    ServerWriteLedger.Normalize(root) + "/",
                    StringComparison.OrdinalIgnoreCase
                )
        )
            return false;

        if (
            ledger.IsServerWritten(fullPath)
            || (oldFullPath is not null && ledger.IsServerWritten(oldFullPath))
        )
        {
            Logger.System(
                $"FileWatcher: Skipping server-written path {fullPath}",
                LogEventLevel.Debug
            );
            return false;
        }

        return true;
    }

    public static bool IsInEncodingOutputDirectory(string fullPath)
    {
        string? directory = Path.GetDirectoryName(fullPath);
        while (!string.IsNullOrEmpty(directory))
        {
            if (EncodingOutputRegex().IsMatch(Path.GetFileName(directory)))
                return true;

            directory = Path.GetDirectoryName(directory);
        }
        return false;
    }

    public static bool IsTemporaryName(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith('~')
            || name.StartsWith("._", StringComparison.Ordinal)
            || TemporaryExtensions.Contains(
                Path.GetExtension(name),
                StringComparer.OrdinalIgnoreCase
            );
    }

    /// <summary>One file event of a library: the filters, then the folder group.</summary>
    public void Handle(
        Library library,
        WatcherChangeTypes type,
        string fullPath,
        string? oldFullPath,
        Func<string, bool> isDirectory
    )
    {
        if (!Accepts(fullPath, oldFullPath))
            return;

        if (!isDirectory(fullPath) && !IsAllowedExtension(library.Type, fullPath))
            return;

        if (type != WatcherChangeTypes.Deleted && !Path.Exists(fullPath))
            return;

        Observe(library, type, fullPath, oldFullPath);
    }

    public static bool IsAllowedExtension(string libraryType, string path)
    {
        string[] extensions = libraryType switch
        {
            MediaTypes.MovieMediaType or MediaTypes.TvMediaType or MediaTypes.AnimeMediaType =>
                VideoExtensions,
            MediaTypes.MusicMediaType => AudioExtensions,
            MediaTypes.InboxMediaType => [.. VideoExtensions, .. AudioExtensions],
            _ => [],
        };
        return extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
    }

    private static readonly string[] VideoExtensions =
    [
        ".mp4",
        ".mkv",
        ".avi",
        ".webm",
        ".mov",
        ".m3u8",
    ];
    private static readonly string[] AudioExtensions = [".mp3", ".flac", ".opus", ".wav", ".m4a"];

    public void Observe(
        Library library,
        WatcherChangeTypes type,
        string fullPath,
        string? oldFullPath = null
    )
    {
        // A download or upload finishing renames its temp file into place: new content.
        if (
            type == WatcherChangeTypes.Renamed
            && (oldFullPath is null || IsTemporaryName(oldFullPath))
        )
        {
            type = WatcherChangeTypes.Created;
            oldFullPath = null;
        }

        string folderPath = Path.GetDirectoryName(fullPath).OrEmpty();
        if (string.IsNullOrEmpty(folderPath))
            return;

        lock (_lock)
        {
            if (!_pending.TryGetValue(folderPath, out Pending? pending))
            {
                pending = new(new(library, folderPath), clock.GetUtcNow());
                _pending[folderPath] = pending;
            }

            pending.Group.Record(
                type,
                fullPath,
                type == WatcherChangeTypes.Renamed ? oldFullPath : null
            );
            pending.LastEvent = clock.GetUtcNow();
            if (type is WatcherChangeTypes.Created or WatcherChangeTypes.Changed)
                pending.Signatures[fullPath] = _probe(fullPath);
        }
    }

    /// <summary>Publishes every group that has been quiet and stable for the quiet period.</summary>
    public async Task TickAsync()
    {
        List<FileChangeGroup> ready = [];
        lock (_lock)
        {
            DateTimeOffset now = clock.GetUtcNow();
            foreach ((string folder, Pending pending) in _pending.ToList())
            {
                if (now - pending.LastEvent < quiet)
                    continue;

                if (!IsStable(pending, now))
                    continue;

                _pending.Remove(folder);
                if (pending.Group.Changes.Count > 0)
                    ready.Add(pending.Group.Snapshot());
            }
        }

        await Task.WhenAll(ready.Select(PublishSafe));
    }

    private bool IsStable(Pending pending, DateTimeOffset now)
    {
        bool stable = true;
        foreach (
            FileChange change in pending
                .Group.Changes.Where(change =>
                    change.ChangeType is WatcherChangeTypes.Created or WatcherChangeTypes.Changed
                )
                .ToList()
        )
        {
            FileSignature? current = _probe(change.FullPath);
            if (current is null)
            {
                pending.Group.Forget(change.FullPath);
                continue;
            }

            if (
                pending.Signatures.TryGetValue(change.FullPath, out FileSignature? seen)
                && seen == current
            )
                continue;

            pending.Signatures[change.FullPath] = current;
            pending.LastEvent = now;
            stable = false;
        }

        return stable;
    }

    private async Task PublishSafe(FileChangeGroup group)
    {
        try
        {
            await publish(group);
        }
        catch (Exception ex)
        {
            Logger.System(
                $"FileWatcher error processing {group.FolderPath}: {ex.Message}",
                LogEventLevel.Error
            );
        }
    }

    private static FileSignature? ProbeDisk(string path)
    {
        try
        {
            if (Directory.Exists(path))
                return new(0, DateTime.MinValue);

            FileInfo info = new(path);
            return info.Exists ? new(info.Length, info.LastWriteTimeUtc) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
