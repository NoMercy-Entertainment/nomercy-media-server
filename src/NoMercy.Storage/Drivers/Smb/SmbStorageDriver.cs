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

using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Storage.Common;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace NoMercy.Storage.Drivers.Smb;

/// <summary>
/// <see cref="IStorageDriver"/> backed by SMBLibrary — a pure-C# SMB2/3 client,
/// so SMB shares work cross-platform with no native dependency. A SMBLibrary
/// session is not re-entrant, so short metadata operations run under a single
/// lock over one throwaway session. Read/write streams instead take their own
/// dedicated session (its own connection + open handle) for the whole transfer,
/// so a multi-GB read or write streams in fixed-size chunks and never buffers the
/// file in memory.
/// </summary>
public sealed class SmbStorageDriver : IStorageDriver, IDisposable
{
    public string BackendLabel => "SMB";

    private readonly SmbDriverConfig _config;
    private readonly ILogger _log;
    private readonly Func<SmbSession> _connect;
    private readonly object _lock = new();
    private bool _disposed;

    // Per-request transfer size handed to the read/write streams, clamped by the
    // SMB2 client's negotiated MaxRead/WriteSize. Defaults to the streams' own
    // default; the throughput sweep sets it to measure the curve. Not exposed on
    // IStorageDriver — an internal tuning seam only.
    internal int StreamChunkSize { get; set; } = 1024 * 1024;

    public SmbStorageDriver(SmbDriverConfig config, ILogger? log = null)
        : this(config, log, connect: null) { }

    // Tests hand in a session over a fake store; production opens a real connection.
    internal SmbStorageDriver(SmbDriverConfig config, ILogger? log, Func<SmbSession>? connect)
    {
        _config = config ?? throw new ArgumentNullException(nameof(config));
        _log = log ?? NullLogger.Instance;
        _connect = connect ?? Connect;
    }

    // ── path helpers ─────────────────────────────────────────────────────────

    // SMB paths inside a share use backslashes and no leading slash. Map a
    // driver-relative path (forward slashes, possibly leading slash) onto the
    // share, honouring the configured BasePath.
    private string ToSmbPath(string path)
    {
        string rel = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        string combined =
            string.IsNullOrEmpty(_config.BasePath) ? rel
            : string.IsNullOrEmpty(rel) ? _config.BasePath
            : $"{_config.BasePath}/{rel}";
        return combined.Replace('/', '\\');
    }

    private static string FromSmbPath(string smbPath) => smbPath.Replace('\\', '/');

    // Relative parent of a path ("" when the path is top-level), in forward-slash
    // form so it can be fed straight back to CreateDirectory.
    private static string ParentOf(string path)
    {
        string rel = (path ?? string.Empty).Replace('\\', '/').Trim('/');
        int slash = rel.LastIndexOf('/');
        return slash < 0 ? string.Empty : rel[..slash];
    }

    // ── connection ───────────────────────────────────────────────────────────

    private SmbSession Connect()
    {
        SMB2Client client = new();
        bool connected = client.Connect(
            ResolveHost(_config.Host),
            SMBTransportType.DirectTCPTransport,
            _config.Port
        );
        if (!connected)
            throw new IOException($"SMB connect failed for {_config.Host}:{_config.Port}");

        NTStatus login = client.Login(
            _config.Domain,
            _config.Username ?? string.Empty,
            _config.Password ?? string.Empty
        );
        if (login != NTStatus.STATUS_SUCCESS)
        {
            client.Disconnect();
            throw new IOException($"SMB login failed for {_config.Host} (status {login})");
        }

        ISMBFileStore store = client.TreeConnect(_config.Share, out NTStatus tree);
        if (tree != NTStatus.STATUS_SUCCESS)
        {
            client.Logoff();
            client.Disconnect();
            throw new IOException(
                $"SMB tree-connect to share '{_config.Share}' failed (status {tree})"
            );
        }

        return new() { Client = client, Store = store };
    }

    private static IPAddress ResolveHost(string host)
    {
        if (IPAddress.TryParse(host, out IPAddress? ip))
            return ip;
        IPAddress[] addresses = Dns.GetHostAddresses(host);
        return addresses.FirstOrDefault(a =>
                a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
            ) ?? addresses.First();
    }

    private T WithSession<T>(Func<SmbSession, T> action)
    {
        lock (_lock)
        {
            using SmbSession session = _connect();
            return action(session);
        }
    }

    // ── existence / metadata ──────────────────────────────────────────────────

    public bool FileExists(string path) =>
        WithSession(s =>
            TryGetInfo(s, ToSmbPath(path), out FileBasicInformation? info, out bool isDir)
            && info is not null
            && !isDir
        );

    public bool DirectoryExists(string path)
    {
        string smb = ToSmbPath(path);
        if (smb.Length == 0)
            return true; // share root
        return WithSession(s => TryGetInfo(s, smb, out _, out bool isDir) && isDir);
    }

    public long GetFileSize(string path) =>
        WithSession(s =>
        {
            OpenForRead(s, ToSmbPath(path), out object handle);
            try
            {
                NTStatus st = s.Store.GetFileInformation(
                    out FileInformation info,
                    handle,
                    FileInformationClass.FileStandardInformation
                );
                SmbStatus.EnsureSuccess(st, $"stat '{path}'");
                return ((FileStandardInformation)info).EndOfFile;
            }
            finally
            {
                s.Store.CloseFile(handle);
            }
        });

    public DateTime GetLastWriteTimeUtc(string path) => GetBasicTimes(path).LastWrite;

    public DateTime GetCreationTimeUtc(string path) => GetBasicTimes(path).Creation;

    public DateTime GetLastAccessTimeUtc(string path) => GetBasicTimes(path).LastAccess;

    private (DateTime Creation, DateTime LastAccess, DateTime LastWrite) GetBasicTimes(
        string path
    ) =>
        WithSession(s =>
        {
            OpenForRead(s, ToSmbPath(path), out object handle);
            try
            {
                NTStatus st = s.Store.GetFileInformation(
                    out FileInformation info,
                    handle,
                    FileInformationClass.FileBasicInformation
                );
                SmbStatus.EnsureSuccess(st, $"stat '{path}'");
                FileBasicInformation b = (FileBasicInformation)info;
                return (
                    (b.CreationTime.Time ?? DateTime.MinValue).ToUniversalTime(),
                    (b.LastAccessTime.Time ?? DateTime.MinValue).ToUniversalTime(),
                    (b.LastWriteTime.Time ?? DateTime.MinValue).ToUniversalTime()
                );
            }
            finally
            {
                s.Store.CloseFile(handle);
            }
        });

    public bool IsHidden(string path) =>
        WithSession(s =>
        {
            if (
                !TryGetInfo(s, ToSmbPath(path), out FileBasicInformation? info, out _)
                || info is null
            )
                return false;
            return info.FileAttributes.HasFlag(FileAttributes.Hidden)
                || info.FileAttributes.HasFlag(FileAttributes.System);
        });

    public string GetFullPath(string path) => FromSmbPath(ToSmbPath(path));

    public string? ResolveLinkTarget(string path) => null; // SMB symlinks not modelled

    // ── streams ────────────────────────────────────────────────────────────────

    public Stream OpenRead(string path)
    {
        // A dedicated connection + open handle streamed for the caller's lifetime
        // (not under the shared metadata lock, and not buffered in memory) — a
        // multi-GB media read stays flat in memory. The stream owns the session.
        SmbSession session = _connect();
        try
        {
            OpenForRead(session, ToSmbPath(path), out object handle);
            return new SmbReadStream(session, handle, path, StreamChunkSize);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    public Stream OpenWrite(string path, bool overwrite)
    {
        if (!overwrite && FileExists(path))
            throw new IOException(
                $"Cannot write to '{path}': file already exists and overwrite is false."
            );

        // Match the other drivers: writing a nested path creates its parents.
        string parent = ParentOf(path);
        if (parent.Length > 0)
            CreateDirectory(parent);

        // A dedicated connection + open handle streamed for the caller's lifetime
        // — bytes go straight to the share via WriteFile at an advancing offset,
        // so a multi-GB write never buffers in memory. The stream owns the session.
        SmbSession session = _connect();
        try
        {
            NTStatus st = session.Store.CreateFile(
                out object handle,
                out FileStatus _,
                ToSmbPath(path),
                AccessMask.GENERIC_WRITE | AccessMask.SYNCHRONIZE,
                FileAttributes.Normal,
                ShareAccess.None,
                CreateDisposition.FILE_OVERWRITE_IF,
                CreateOptions.FILE_NON_DIRECTORY_FILE | CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null
            );
            SmbStatus.EnsureSuccess(st, $"create '{path}'");
            return new SmbWriteStream(session, handle, path, StreamChunkSize);
        }
        catch
        {
            session.Dispose();
            throw;
        }
    }

    // ── mutations ──────────────────────────────────────────────────────────────

    public void CreateDirectory(string path) =>
        WithSession(s =>
        {
            // Create each missing segment so nested dirs work like the other drivers.
            string[] segments = ToSmbPath(path).Split('\\', StringSplitOptions.RemoveEmptyEntries);
            string current = string.Empty;
            foreach (string segment in segments)
            {
                current = current.Length == 0 ? segment : $"{current}\\{segment}";
                NTStatus st = s.Store.CreateFile(
                    out object handle,
                    out FileStatus _,
                    current,
                    AccessMask.GENERIC_READ,
                    FileAttributes.Directory,
                    ShareAccess.Read | ShareAccess.Write,
                    CreateDisposition.FILE_OPEN_IF,
                    CreateOptions.FILE_DIRECTORY_FILE,
                    null
                );
                if (st == NTStatus.STATUS_SUCCESS)
                    s.Store.CloseFile(handle);
                else if (st != NTStatus.STATUS_OBJECT_NAME_COLLISION)
                    SmbStatus.EnsureSuccess(st, $"mkdir '{current}'");
            }
            return 0;
        });

    public void DeleteFile(string path) => Delete(ToSmbPath(path), directory: false);

    public void DeleteDirectory(string path, bool recursive)
    {
        if (recursive)
            foreach (
                string child in EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly)
            )
            {
                if (DirectoryExists(child))
                    DeleteDirectory(child, recursive: true);
                else
                    DeleteFile(child);
            }
        Delete(ToSmbPath(path), directory: true);
    }

    private void Delete(string smbPath, bool directory) =>
        WithSession(s =>
        {
            NTStatus st = s.Store.CreateFile(
                out object handle,
                out FileStatus _,
                smbPath,
                AccessMask.DELETE,
                directory ? FileAttributes.Directory : FileAttributes.Normal,
                // Tolerate a still-open read stream (which shares Read | Delete):
                // the delete-open must itself allow those to coexist, otherwise a
                // delete-while-reading raises SHARING_VIOLATION.
                ShareAccess.Read
                    | ShareAccess.Write
                    | ShareAccess.Delete,
                CreateDisposition.FILE_OPEN,
                directory
                    ? CreateOptions.FILE_DIRECTORY_FILE
                    : CreateOptions.FILE_NON_DIRECTORY_FILE,
                null
            );
            SmbStatus.EnsureSuccess(st, $"open-for-delete '{FromSmbPath(smbPath)}'");
            try
            {
                FileDispositionInformation disposition = new() { DeletePending = true };
                NTStatus dst = s.Store.SetFileInformation(handle, disposition);
                SmbStatus.EnsureSuccess(dst, $"delete '{FromSmbPath(smbPath)}'");
            }
            finally
            {
                s.Store.CloseFile(handle);
            }
            return 0;
        });

    public void MoveFile(string source, string destination) => Rename(source, destination);

    public void MoveDirectory(string source, string destination) => Rename(source, destination);

    private void Rename(string source, string destination)
    {
        // SMB rename won't create the destination's parent; match the other
        // drivers and ensure it exists first.
        string destParent = ParentOf(destination);
        if (destParent.Length > 0)
            CreateDirectory(destParent);

        WithSession(s =>
        {
            string srcSmb = ToSmbPath(source);
            NTStatus st = s.Store.CreateFile(
                out object handle,
                out FileStatus _,
                srcSmb,
                AccessMask.GENERIC_ALL | AccessMask.DELETE | AccessMask.SYNCHRONIZE,
                FileAttributes.Normal,
                ShareAccess.None,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
                null
            );
            SmbStatus.EnsureSuccess(st, $"open-for-rename '{source}'");
            try
            {
                FileRenameInformationType2 rename = new()
                {
                    ReplaceIfExists = true,
                    FileName = ToSmbPath(destination),
                };
                NTStatus rst = s.Store.SetFileInformation(handle, rename);
                SmbStatus.EnsureSuccess(rst, $"rename '{source}' -> '{destination}'");
            }
            finally
            {
                s.Store.CloseFile(handle);
            }
            return 0;
        });
    }

    public void CopyFile(string source, string destination, bool overwrite)
    {
        // SMB has no server-side copy in SMBLibrary's surface — stream the bytes
        // from source to destination without buffering the whole file.
        using Stream r = OpenRead(source);
        using Stream w = OpenWrite(destination, overwrite);
        r.CopyTo(w);
    }

    // ── enumeration ────────────────────────────────────────────────────────────

    public IEnumerable<string> EnumerateFileSystemEntries(
        string directory,
        string searchPattern,
        SearchOption option
    )
    {
        List<string> results = [];
        CollectEntries(directory, searchPattern, option, results);
        return results;
    }

    private void CollectEntries(
        string directory,
        string searchPattern,
        SearchOption option,
        List<string> results
    )
    {
        string dirRel = (directory ?? string.Empty).Replace('\\', '/').Trim('/');
        List<(string Name, bool IsDir)> children = WithSession(s =>
        {
            string smbDir = ToSmbPath(dirRel);
            NTStatus st = s.Store.CreateFile(
                out object handle,
                out FileStatus _,
                smbDir,
                AccessMask.GENERIC_READ,
                FileAttributes.Directory,
                ShareAccess.Read | ShareAccess.Write,
                CreateDisposition.FILE_OPEN,
                CreateOptions.FILE_DIRECTORY_FILE,
                null
            );
            // Enumerating a missing directory yields nothing, like the other
            // drivers — not an error.
            if (
                st is NTStatus.STATUS_OBJECT_NAME_NOT_FOUND or NTStatus.STATUS_OBJECT_PATH_NOT_FOUND
            )
                return [];
            SmbStatus.EnsureSuccess(st, $"open-dir '{directory}'");
            try
            {
                // The server applies the search pattern to folder names too, so
                // a filtered query hides every folder the pattern does not name
                // and the recursion below would never reach the files in them
                // (and a folder with no match at all answers STATUS_NO_SUCH_FILE).
                // List everything and match names here, like the other drivers.
                NTStatus qst = s.Store.QueryDirectory(
                    out List<QueryDirectoryFileInformation> entries,
                    handle,
                    "*",
                    FileInformationClass.FileDirectoryInformation
                );
                if (qst != NTStatus.STATUS_SUCCESS && qst != NTStatus.STATUS_NO_MORE_FILES)
                    SmbStatus.EnsureSuccess(qst, $"list '{directory}'");

                List<(string, bool)> found = [];
                foreach (QueryDirectoryFileInformation entry in entries)
                {
                    FileDirectoryInformation info = (FileDirectoryInformation)entry;
                    if (info.FileName is "." or "..")
                        continue;
                    bool isDir = info.FileAttributes.HasFlag(FileAttributes.Directory);
                    found.Add((info.FileName, isDir));
                }
                return found;
            }
            finally
            {
                s.Store.CloseFile(handle);
            }
        });

        foreach ((string name, bool isDir) in children)
        {
            string childRel = dirRel.Length == 0 ? name : $"{dirRel}/{name}";
            if (StoragePatternMatcher.Matches(name, searchPattern))
                results.Add("/" + childRel);
            if (option == SearchOption.AllDirectories && isDir)
                CollectEntries(childRel, searchPattern, option, results);
        }
    }

    // ── internals ──────────────────────────────────────────────────────────────

    private static void OpenForRead(SmbSession s, string smbPath, out object handle)
    {
        NTStatus st = s.Store.CreateFile(
            out handle,
            out FileStatus _,
            smbPath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            FileAttributes.Normal,
            // Allow a concurrent delete/rename while a read stream holds the file
            // open — the streaming read keeps the handle for its whole lifetime,
            // so without share-delete a delete-after-read raises SHARING_VIOLATION.
            ShareAccess.Read | ShareAccess.Delete,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null
        );
        SmbStatus.EnsureSuccess(st, $"open '{FromSmbPath(smbPath)}'");
    }

    private static bool TryGetInfo(
        SmbSession s,
        string smbPath,
        out FileBasicInformation? info,
        out bool isDirectory
    )
    {
        info = null;
        isDirectory = false;
        NTStatus st = s.Store.CreateFile(
            out object handle,
            out FileStatus _,
            smbPath,
            AccessMask.GENERIC_READ | AccessMask.SYNCHRONIZE,
            FileAttributes.Normal,
            ShareAccess.Read | ShareAccess.Write,
            CreateDisposition.FILE_OPEN,
            CreateOptions.FILE_SYNCHRONOUS_IO_NONALERT,
            null
        );
        if (st != NTStatus.STATUS_SUCCESS)
            return false;
        try
        {
            NTStatus bst = s.Store.GetFileInformation(
                out FileInformation basic,
                handle,
                FileInformationClass.FileBasicInformation
            );
            if (bst != NTStatus.STATUS_SUCCESS)
                return false;
            info = (FileBasicInformation)basic;
            isDirectory = info.FileAttributes.HasFlag(FileAttributes.Directory);
            return true;
        }
        finally
        {
            s.Store.CloseFile(handle);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
    }
}
