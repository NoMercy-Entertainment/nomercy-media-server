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
/// The real local driver, with a switch that makes a chosen move fail, to put a
/// restore through a failure at a precise point of its swap.
/// </summary>
public sealed class FaultyStorageDriver : IStorageDriver
{
    private readonly LocalStorageDriver _inner = new();

    /// <summary>Called with (from, to) before every file or folder move; true makes it throw.</summary>
    public Func<string, string, bool> FailMove { get; set; } = (_, _) => false;

    /// <summary>Every move that went through, in order.</summary>
    public List<(string From, string To)> Moves { get; } = [];

    public bool FileExists(string path) => _inner.FileExists(path);

    public bool DirectoryExists(string path) => _inner.DirectoryExists(path);

    public void CreateDirectory(string path) => _inner.CreateDirectory(path);

    public void DeleteFile(string path) => _inner.DeleteFile(path);

    public void DeleteDirectory(string path, bool recursive) =>
        _inner.DeleteDirectory(path, recursive);

    public long GetFileSize(string path) => _inner.GetFileSize(path);

    public DateTime GetLastWriteTimeUtc(string path) => _inner.GetLastWriteTimeUtc(path);

    public DateTime GetCreationTimeUtc(string path) => _inner.GetCreationTimeUtc(path);

    public DateTime GetLastAccessTimeUtc(string path) => _inner.GetLastAccessTimeUtc(path);

    public Stream OpenRead(string path) => _inner.OpenRead(path);

    public Stream OpenWrite(string path, bool overwrite) => _inner.OpenWrite(path, overwrite);

    public void MoveFile(string source, string destination)
    {
        Guard(source, destination);
        _inner.MoveFile(source, destination);
        Moves.Add((source, destination));
    }

    public void CopyFile(string source, string destination, bool overwrite) =>
        _inner.CopyFile(source, destination, overwrite);

    public IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        SearchOption option
    ) => _inner.EnumerateFileSystemEntries(directory, searchPattern, option);

    public string GetFullPath(string path) => _inner.GetFullPath(path);

    public string? ResolveLinkTarget(string path) => _inner.ResolveLinkTarget(path);

    public bool IsHidden(string path) => _inner.IsHidden(path);

    public void MoveDirectory(string source, string destination)
    {
        Guard(source, destination);
        _inner.MoveDirectory(source, destination);
        Moves.Add((source, destination));
    }

    private void Guard(string source, string destination)
    {
        if (FailMove(source, destination))
            throw new IOException($"Simulated failure moving '{source}' to '{destination}'.");
    }
}
