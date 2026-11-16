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

using NoMercy.Storage;
using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.Service.ServerBackups;

/// <summary>
/// The real local driver, with switches that make a chosen move, delete or
/// write fail, or make the whole "process" die after a number of swap renames,
/// to put a restore through a failure at a precise point.
/// </summary>
public sealed class FaultyStorageDriver : IStorageDriver
{
    private readonly LocalStorageDriver _inner = new();
    private bool _journalWritten;
    private bool _journalDeleted;

    /// <summary>Called with (from, to) before every file or folder move; true makes it throw.</summary>
    public Func<string, string, bool> FailMove { get; set; } = (_, _) => false;

    /// <summary>Called before every file delete; true makes it throw.</summary>
    public Func<string, bool> FailDelete { get; set; } = _ => false;

    /// <summary>Called before every file opened for writing; true makes it throw (a full disk).</summary>
    public Func<string, bool> FailOpenWrite { get; set; } = _ => false;

    /// <summary>
    /// Once the journal is on disk and this many swap renames have happened,
    /// every change to the disk fails, as if the process had died there: what is
    /// on disk is exactly what a crash would leave. Reads keep working.
    /// </summary>
    public int? DeadAfterSwapMoves { get; set; }

    /// <summary>
    /// Once the journal has been deleted (the commit point), every change to the
    /// disk fails: a crash right after a restore was applied.
    /// </summary>
    public bool DeadAfterJournalDeleted { get; set; }

    /// <summary>Called before every folder delete; true makes it throw (a file held open).</summary>
    public Func<string, bool> FailDeleteDirectory { get; set; } = _ => false;

    /// <summary>Every move that went through, in order.</summary>
    public List<(string From, string To)> Moves { get; } = [];

    public int SwapMoves =>
        Moves.Count(move => !move.From.EndsWith(".tmp", StringComparison.Ordinal));

    private bool Dead =>
        (_journalWritten && DeadAfterSwapMoves is int limit && SwapMoves >= limit)
        || (DeadAfterJournalDeleted && _journalDeleted);

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public void CreateDirectory(string path)
    {
        GuardAlive();
        _inner.CreateDirectory(path);
    }

    public void DeleteFile(string path)
    {
        GuardAlive();
        if (FailDelete(path))
            throw new IOException($"Simulated failure deleting '{path}'.");
        _inner.DeleteFile(path);
        if (path.EndsWith("journal.json", StringComparison.Ordinal))
            _journalDeleted = true;
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        GuardAlive();
        if (FailDeleteDirectory(path))
            throw new IOException($"Simulated failure deleting the folder '{path}'.");
        _inner.DeleteDirectory(path, recursive);
    }

    public long GetFileSize(string path) => _inner.GetFileSize(path);

    public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

    public DateTime GetCreationTimeUtc(string path) => _inner.GetCreationTimeUtc(path);

    public DateTime GetLastAccessTimeUtc(string path) => _inner.GetLastAccessTimeUtc(path);

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public Stream OpenWrite(string path, bool overwrite)
    {
        GuardAlive();
        if (FailOpenWrite(path))
            throw new IOException("There is not enough space on the disk.");
        return _inner.OpenWrite(path, overwrite);
    }

    public void MoveFile(string source, string destination)
    {
        GuardMove(source, destination);
        _inner.MoveFile(source, destination);
        Record(source, destination);
    }

    public void ReplaceFile(string source, string destination)
    {
        GuardMove(source, destination);
        _inner.ReplaceFile(source, destination);
        Record(source, destination);
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        GuardAlive();
        _inner.CopyFile(source, destination, overwrite);
    }

    public IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        SearchOption option
    ) => _inner.EnumerateFileSystemEntries(directory, searchPattern, option);

    public string GetFullPath(string path) => _inner.GetFullPath(path);

    public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);

    public bool IsHidden(string path) => _inner.IsHidden(path);

    public bool TryRestrictToOwner(string path) => _inner.TryRestrictToOwner(path);

    public void MoveDirectory(string source, string destination)
    {
        GuardMove(source, destination);
        _inner.MoveDirectory(source, destination);
        Record(source, destination);
    }

    private void Record(string source, string destination)
    {
        Moves.Add((source, destination));
        if (destination.EndsWith("journal.json", StringComparison.Ordinal))
            _journalWritten = true;
    }

    private void GuardMove(string source, string destination)
    {
        GuardAlive();
        if (FailMove(source, destination))
            throw new IOException($"Simulated failure moving '{source}' to '{destination}'.");
    }

    private void GuardAlive()
    {
        if (Dead)
            throw new IOException("Simulated crash: the process is gone.");
    }
}
