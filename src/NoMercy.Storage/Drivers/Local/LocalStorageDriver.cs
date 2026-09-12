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

using NoMercy.Storage.Common;

namespace NoMercy.Storage.Drivers.Local;

public sealed class LocalStorageDriver : IStorageDriver
{
    // Keyed by the exact directory path. A release's tracks each dispatch their
    // own MusicEncodeJob and every one of them calls CreateDirectory on the SAME
    // album folder — with several running in parallel, two workers hit an empty
    // path at once and the network share hands one of them ERROR_IO_DEVICE
    // instead of the ordinary "already exists" no-op a local disk gives for a
    // plain race. Retrying alone still lost that race under load; only one
    // thread may create a given path at a time now.
    //
    // The shared keyed lock, not a bare `lock`: Directory.CreateDirectory
    // against a genuinely unresponsive network mount can block indefinitely,
    // and a plain lock has no way to time out of that — one wedged call would
    // then hold every future create for the same album folder forever. The
    // bounded acquire surfaces a timeout as an ordinary failure the job queue's
    // own retry/dead-letter path already handles, and it prunes a path's entry
    // once nothing is creating under it any more.
    private static readonly KeyedAsyncLock DirectoryLocks = new();

    private static readonly TimeSpan DirectoryLockTimeout = TimeSpan.FromSeconds(30);

    public string BackendLabel => "Local";

    public char DirectorySeparator => Path.DirectorySeparatorChar;

    // Not Path.Combine: it returns the child alone whenever the child looks rooted,
    // and every stored Filename starts with a separator ("/Movie.NoMercy.m3u8"), so
    // joining a folder to a filename silently produced the bare filename. The
    // IStorageDriver contract is a plain join with the child's leading separators
    // trimmed — remote drivers already behave that way.
    public string CombinePath(string parent, string child)
    {
        if (string.IsNullOrEmpty(child))
            return parent;
        if (string.IsNullOrEmpty(parent))
            return child;
        return Path.Combine(parent.TrimEnd('/', '\\'), child.TrimStart('/', '\\'));
    }

    public bool FileExists(string path) => File.Exists(path);

    public bool DirectoryExists(string path) => Directory.Exists(path);

    // Retried, unlike every other call here — a folder on a network share the OS
    // presents as a local path (an NFS/SMB mount, not NoMercy's own SmbStorageDriver)
    // surfaces a transient link hiccup as IOException "Kan de opdracht niet
    // uitvoeren door een fout in een I/O-apparaat" (ERROR_IO_DEVICE), and the very
    // next Directory.CreateDirectory on the identical path succeeds. NfsStorageDriver
    // already retries its own protocol-level EXPIRED faults; this mirrors that for
    // the plain System.IO path, the one driver with none.
    public void CreateDirectory(string path)
    {
        // The key is the path, so the TimeoutException the acquire throws when
        // the wait runs out still says which directory this call gave up on.
        using IDisposable gate = DirectoryLocks.Acquire(
            path.TrimEnd('/', '\\'),
            DirectoryLockTimeout
        );

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Directory.CreateDirectory(path);
                return;
            }
            catch (IOException) when (attempt < 3)
            {
                Thread.Sleep(attempt * 250);
            }
        }
    }

    public void DeleteFile(string path) => File.Delete(path);

    public void DeleteDirectory(string path, bool recursive) => Directory.Delete(path, recursive);

    public long GetFileSize(string path) => new FileInfo(path).Length;

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(path);

    // File.GetCreation*/Directory.GetCreation* are only permitted here — all callers must go through IStorageDriver.
    public DateTime GetCreationTimeUtc(string path) =>
        Directory.Exists(path) ? Directory.GetCreationTimeUtc(path) : File.GetCreationTimeUtc(path);

    // Remote backends (S3, NFS, WebDAV) don't reliably expose atime — they return LastWriteTime as fallback.
    public DateTime GetLastAccessTimeUtc(string path) =>
        Directory.Exists(path)
            ? Directory.GetLastAccessTimeUtc(path)
            : File.GetLastAccessTimeUtc(path);

    // FileShare.ReadWrite | FileShare.Delete is critical on Windows. Default
    // FileShare.Read blocks concurrent writes/deletes for the stream's entire
    // lifetime — long ExoPlayer Range reads keep this open for hours, which
    // would block encoder output, library reorg, replace-on-merge, etc.
    public Stream OpenRead(string path) =>
        new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 64 * 1024,
            useAsync: true
        );

    public Stream OpenWrite(string path, bool overwrite) =>
        new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            useAsync: true
        );

    // Local files are already on local FS; no staging needed.
    public Task<LocalPathLease> AcquireLocalPathAsync(string path, CancellationToken ct) =>
        Task.FromResult(new LocalPathLease(path));

    public void MoveFile(string source, string destination) =>
        File.Move(source, destination, overwrite: false);

    public void CopyFile(string source, string destination, bool overwrite) =>
        File.Copy(source, destination, overwrite);

    public IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        SearchOption option
    ) =>
        // Path Contract: List on a non-existent directory returns empty,
        // never throws. Directory.EnumerateFileSystemEntries throws
        // DirectoryNotFoundException on a missing directory; guard it.
        Directory.Exists(directory)
            ? Directory.EnumerateFileSystemEntries(directory, searchPattern, option)
            : [];

    public IEnumerable<StorageEntryInfo> EnumerateEntries(
        string directory,
        string searchPattern,
        SearchOption option
    )
    {
        // Single-pass metadata: DirectoryInfo enumeration carries size,
        // last-write and is-directory from the OS readdir, so there is no extra
        // stat per entry (the default IStorageDriver implementation does N extra
        // DirectoryExists/GetFileSize/GetLastWriteTimeUtc calls). Same empty-on-
        // missing-directory contract as EnumerateFileSystemEntries.
        if (!Directory.Exists(directory))
            yield break;

        DirectoryInfo root = new(directory);
        foreach (FileSystemInfo info in root.EnumerateFileSystemInfos(searchPattern, option))
        {
            bool isDir = info is DirectoryInfo;
            long size = info is FileInfo file ? file.Length : 0L;
            yield return new(info.FullName, isDir, size, info.LastWriteTimeUtc);
        }
    }

    public string GetFullPath(string path) => Path.GetFullPath(path);

    public string? ResolveLinkTarget(string path)
    {
        try
        {
            FileSystemInfo? info =
                File.Exists(path) ? new FileInfo(path)
                : Directory.Exists(path) ? new DirectoryInfo(path)
                : null;
            if (info?.LinkTarget is null)
                return null;
            FileSystemInfo? real = info.ResolveLinkTarget(returnFinalTarget: true);
            return real is null ? null : Path.GetFullPath(real.FullName);
        }
        catch
        {
            return null;
        }
    }

    public bool IsHidden(string path)
    {
        try
        {
            FileAttributes attrs = File.GetAttributes(path);
            return (attrs & (FileAttributes.Hidden | FileAttributes.System)) != 0;
        }
        catch
        {
            return false;
        }
    }

    public void MoveDirectory(string source, string destination) =>
        Directory.Move(source, destination);
}
