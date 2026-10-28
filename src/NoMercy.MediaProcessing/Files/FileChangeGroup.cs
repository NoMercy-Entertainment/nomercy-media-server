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

using NoMercy.Database.Models.Libraries;

namespace NoMercy.MediaProcessing.Files;

public class FileChangeGroup(Library library, string folderPath)
{
    private readonly List<FileChange> _changes = [];

    public string FolderPath { get; set; } = folderPath;
    public Library Library { get; set; } = library;
    public Timer? Timer { get; set; }
    public IReadOnlyList<FileChange> Changes => _changes;

    /// <summary>One action per path, the latest; a deleted rename target also deletes its source.</summary>
    public void Record(WatcherChangeTypes type, string fullPath, string? oldFullPath)
    {
        int rename = _changes.FindIndex(change =>
            change.FullPath == fullPath
            && change is { ChangeType: WatcherChangeTypes.Renamed, OldFullPath: not null }
        );
        if (rename >= 0 && type == WatcherChangeTypes.Deleted)
            _changes[rename] = new(WatcherChangeTypes.Deleted, _changes[rename].OldFullPath!);
        else
            _changes.RemoveAll(change => change.FullPath == fullPath);

        _changes.Add(new(type, fullPath, oldFullPath));
    }

    public FileChangeGroup Snapshot()
    {
        FileChangeGroup snapshot = new(Library, FolderPath);
        snapshot._changes.AddRange(_changes);
        return snapshot;
    }
}
