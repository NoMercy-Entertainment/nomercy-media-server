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
using System.Runtime.InteropServices;
using System.Text;
using NoMercy.Storage.Drivers.Nfs.Interop;

namespace NoMercy.Tests.Storage.Faults;

/// <summary>
/// In-memory <see cref="ILibNfs"/> for fault-injection tests. Backs a tiny
/// path → bytes map so OpenRead/OpenWrite/Stat behave like a real NFS export,
/// and exposes a per-method script ("call N returns rc=X with error=Y") so
/// tests can deterministically reproduce NFS4ERR_EXPIRED / BAD_SEQID etc.
/// at any specific call site.
///
/// Calls are tracked in <see cref="CallCounts"/> so tests can assert the
/// driver retried (or didn't) the expected number of times.
/// </summary>
internal sealed class FaultyLibNfs : ILibNfs
{
    private readonly ConcurrentDictionary<string, byte[]> _files = new();
    private readonly ConcurrentDictionary<string, bool> _dirs = new();
    private readonly ConcurrentDictionary<string, DateTime> _mtimes = new();
    private readonly ConcurrentDictionary<IntPtr, FileHandle> _openHandles = new();
    private readonly ConcurrentDictionary<IntPtr, DirIter> _openDirs = new();
    private long _nextHandle = 1;
    private long _nextContext = 1;

    private sealed class DirIter
    {
        public required List<string> Entries { get; init; }
        public required List<bool> EntryIsDir { get; init; }
        public int Cursor;
        public IntPtr ScratchEntry; // unmanaged NfsDirent struct
        public IntPtr ScratchName; // unmanaged UTF-8 bytes
    }

    public string CurrentError { get; private set; } = string.Empty;

    /// <summary>
    /// Per-method call counts — assert against these in tests
    /// (e.g. "Open was called twice" = original + post-remount retry).
    /// </summary>
    public Dictionary<string, int> CallCounts { get; } = new();

    /// <summary>
    /// When &gt; 0, every libnfs call sleeps this long inside the protected
    /// region. Forces concurrency races to surface in stress tests so the
    /// driver's lock guarantees are observable.
    /// </summary>
    public TimeSpan ArtificialLatency { get; set; } = TimeSpan.Zero;

    /// <summary>
    /// Largest number of libnfs calls observed running simultaneously — proves
    /// the driver serializes access to the shared context. The driver-side
    /// SemaphoreSlim should pin this to 1; anything higher is a contract bug.
    /// </summary>
    public int MaxConcurrentCalls => _maxConcurrent;

    private int _currentConcurrent;
    private int _maxConcurrent;

    /// <summary>
    /// Scripted responses keyed by "Method:callIndex" (0-based).
    /// E.g. <c>Faults["Open:0"] = (-11, "NFS4ERR_EXPIRED")</c> makes the FIRST
    /// Open call return -11 with that error. Other calls hit the real backend.
    /// </summary>
    public Dictionary<string, (int rc, string error)> Faults { get; } = new();

    public IReadOnlyDictionary<string, byte[]> Files => _files;

    public void Seed(string path, byte[] content)
    {
        string key = Normalise(path);
        _files[key] = content;
        _mtimes[key] = DateTime.UtcNow;
        EnsureParentDirs(path);
    }

    public void SeedDir(string path)
    {
        string key = Normalise(path);
        _dirs[key] = true;
        _mtimes[key] = DateTime.UtcNow;
    }

    private static string Normalise(string path)
    {
        string normalised = path.Replace('\\', '/');
        if (!normalised.StartsWith('/'))
            normalised = "/" + normalised;
        return normalised.TrimEnd('/');
    }

    private void EnsureParentDirs(string path) => EnsureParentDirsKey(Normalise(path));

    private void EnsureParentDirsKey(string normalised)
    {
        int idx = normalised.LastIndexOf('/');
        while (idx > 0)
        {
            _dirs[normalised[..idx]] = true;
            idx = normalised[..idx].LastIndexOf('/');
        }
        _dirs["/"] = true;
    }

    private bool TryFault(string method, out int rc, out string err)
    {
        // Track concurrent execution so stress tests can prove the driver's
        // lock holds the libnfs context single-threaded.
        int current = Interlocked.Increment(ref _currentConcurrent);
        int max = _maxConcurrent;
        while (current > max)
            max = Interlocked.CompareExchange(ref _maxConcurrent, current, max);

        try
        {
            int index;
            lock (CallCounts)
            {
                index = CallCounts.GetValueOrDefault(method, 0);
                CallCounts[method] = index + 1;
            }

            if (ArtificialLatency > TimeSpan.Zero)
                Thread.Sleep(ArtificialLatency);

            (int rc, string error) fault;
            bool faultPresent;
            lock (Faults)
            {
                faultPresent = Faults.TryGetValue($"{method}:{index}", out fault);
            }
            if (faultPresent)
            {
                rc = fault.rc;
                err = fault.error;
                CurrentError = err;
                return true;
            }
            rc = 0;
            err = string.Empty;
            return false;
        }
        finally
        {
            Interlocked.Decrement(ref _currentConcurrent);
        }
    }

    private sealed class FileHandle
    {
        public string Path { get; init; } = string.Empty;
        public int Mode { get; init; }
        public long Position { get; set; }
    }

    // -----------------------------------------------------------------------
    // ILibNfs
    // -----------------------------------------------------------------------

    public IntPtr InitContext()
    {
        if (TryFault(nameof(InitContext), out _, out _))
            return IntPtr.Zero;
        return new(Interlocked.Increment(ref _nextContext));
    }

    public void DestroyContext(IntPtr nfs)
    {
        TryFault(nameof(DestroyContext), out _, out _);
    }

    public int Mount(IntPtr nfs, string server, string exportPath)
    {
        if (TryFault(nameof(Mount), out int rc, out _))
            return rc;
        return 0;
    }

    public int Umount(IntPtr nfs)
    {
        TryFault(nameof(Umount), out _, out _);
        return 0;
    }

    public void SetUid(IntPtr nfs, int uid) => TryFault(nameof(SetUid), out _, out _);

    public void SetGid(IntPtr nfs, int gid) => TryFault(nameof(SetGid), out _, out _);

    public int SetVersion(IntPtr nfs, int version)
    {
        if (TryFault(nameof(SetVersion), out int rc, out _))
            return rc;
        return 0;
    }

    /// <summary>
    /// NFSv4 client name applied to each context via <c>nfs_set_client_name</c>,
    /// keyed by context pointer. Isolated read contexts must each receive a
    /// UNIQUE name so their open-owner seqid sequences stay independent on the
    /// server (a shared name collides as NFS4ERR_BAD_SEQID under a parallel scan).
    /// </summary>
    public ConcurrentDictionary<IntPtr, string> ClientNames { get; } = new();

    public void SetClientName(IntPtr nfs, string id) => ClientNames[nfs] = id;

    public void SetVerifier(IntPtr nfs, string verifier) { }

    public string GetError(IntPtr nfs) => CurrentError;

    public int Stat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat)
    {
        if (TryFault(nameof(Stat64), out int rc, out _))
        {
            stat = default;
            return rc;
        }

        string key = Normalise(path);
        stat = default;

        // Recorded write time so LastModifiedAsync returns a meaningful
        // recent timestamp instead of the Unix epoch.
        DateTime mtime = _mtimes.TryGetValue(key, out DateTime t) ? t : DateTime.UtcNow;
        ulong mtimeSec = (ulong)new DateTimeOffset(mtime, TimeSpan.Zero).ToUnixTimeSeconds();

        if (_files.TryGetValue(key, out byte[]? content))
        {
            stat = new()
            {
                Size = (ulong)content.Length,
                Mode = 0x8000, /* S_IFREG << 12 */
                MtimeSec = mtimeSec,
                CtimeSec = mtimeSec,
                AtimeSec = mtimeSec,
            };
            return 0;
        }
        if (_dirs.ContainsKey(key))
        {
            stat = new()
            {
                Mode = 0x4000, /* S_IFDIR << 12 */
                MtimeSec = mtimeSec,
                CtimeSec = mtimeSec,
                AtimeSec = mtimeSec,
            };
            return 0;
        }

        CurrentError = "NFS4ERR_NOENT";
        return -2;
    }

    public int Lstat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat) =>
        Stat64(nfs, path, out stat);

    public int OpenDir(IntPtr nfs, string path, out IntPtr dir)
    {
        if (TryFault(nameof(OpenDir), out int rc, out _))
        {
            dir = IntPtr.Zero;
            return rc;
        }

        string parent = Normalise(path);
        string parentPrefix = parent == "/" ? "/" : parent + "/";

        // Collect immediate children (not recursive).
        List<string> entries = [];
        List<bool> entryIsDir = [];

        foreach (string fileKey in _files.Keys)
        {
            if (!fileKey.StartsWith(parentPrefix, StringComparison.Ordinal))
                continue;
            string remainder = fileKey.Substring(parentPrefix.Length);
            if (remainder.Length == 0 || remainder.Contains('/'))
                continue;
            entries.Add(remainder);
            entryIsDir.Add(false);
        }

        foreach (string dirKey in _dirs.Keys)
        {
            if (dirKey == parent)
                continue;
            if (!dirKey.StartsWith(parentPrefix, StringComparison.Ordinal))
                continue;
            string remainder = dirKey.Substring(parentPrefix.Length);
            if (remainder.Length == 0 || remainder.Contains('/'))
                continue;
            entries.Add(remainder);
            entryIsDir.Add(true);
        }

        IntPtr handle = new(Interlocked.Increment(ref _nextHandle));
        _openDirs[handle] = new() { Entries = entries, EntryIsDir = entryIsDir };
        dir = handle;
        return 0;
    }

    public IntPtr ReadDir(IntPtr nfs, IntPtr dir)
    {
        TryFault(nameof(ReadDir), out _, out _);

        if (!_openDirs.TryGetValue(dir, out DirIter? iter))
            return IntPtr.Zero;

        if (iter.Cursor >= iter.Entries.Count)
            return IntPtr.Zero;

        string name = iter.Entries[iter.Cursor];
        bool isDir = iter.EntryIsDir[iter.Cursor];
        iter.Cursor++;

        // Free any previous scratch allocations before allocating fresh ones
        // for this entry — the driver only reads the most recently returned
        // pointer before calling ReadDir again, so we can reuse the slot.
        FreeScratch(iter);

        // Allocate UTF-8 name buffer in unmanaged memory.
        byte[] nameBytes = Encoding.UTF8.GetBytes(name + "\0");
        IntPtr namePtr = Marshal.AllocHGlobal(nameBytes.Length);
        Marshal.Copy(nameBytes, 0, namePtr, nameBytes.Length);
        iter.ScratchName = namePtr;

        // Allocate the NfsDirent struct itself.
        LibNfs.NfsDirent entry = new()
        {
            Next = IntPtr.Zero,
            Name = namePtr,
            Inode = (ulong)iter.Cursor,
            // libnfs ftype3: NF3REG=1, NF3DIR=2.
            Type = isDir ? 2u : 1u,
        };
        IntPtr entryPtr = Marshal.AllocHGlobal(Marshal.SizeOf<LibNfs.NfsDirent>());
        Marshal.StructureToPtr(entry, entryPtr, fDeleteOld: false);
        iter.ScratchEntry = entryPtr;

        return entryPtr;
    }

    public void CloseDir(IntPtr nfs, IntPtr dir)
    {
        TryFault(nameof(CloseDir), out _, out _);
        if (_openDirs.TryRemove(dir, out DirIter? iter))
            FreeScratch(iter);
    }

    private static void FreeScratch(DirIter iter)
    {
        if (iter.ScratchEntry != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(iter.ScratchEntry);
            iter.ScratchEntry = IntPtr.Zero;
        }
        if (iter.ScratchName != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(iter.ScratchName);
            iter.ScratchName = IntPtr.Zero;
        }
    }

    public int MkDir(IntPtr nfs, string path)
    {
        if (TryFault(nameof(MkDir), out int rc, out _))
            return rc;
        _dirs[Normalise(path)] = true;
        return 0;
    }

    public int RmDir(IntPtr nfs, string path)
    {
        if (TryFault(nameof(RmDir), out int rc, out _))
            return rc;
        _dirs.TryRemove(Normalise(path), out _);
        return 0;
    }

    public int Open(IntPtr nfs, string path, int flags, out IntPtr fh)
    {
        if (TryFault(nameof(Open), out int rc, out _))
        {
            fh = IntPtr.Zero;
            return rc;
        }

        string key = Normalise(path);
        int createFlag =
            OperatingSystem.IsWindows() ? 0x100
            : OperatingSystem.IsMacOS() ? 0x200
            : 0x40;
        int truncateFlag = OperatingSystem.IsMacOS() ? 0x400 : 0x200;
        int exclusiveFlag =
            OperatingSystem.IsWindows() ? 0x400
            : OperatingSystem.IsMacOS() ? 0x800
            : 0x80;
        bool isCreate = (flags & createFlag) != 0;
        bool isTrunc = (flags & truncateFlag) != 0;
        bool isExcl = (flags & exclusiveFlag) != 0;

        if (isCreate && isExcl && _files.ContainsKey(key))
        {
            fh = IntPtr.Zero;
            CurrentError = "EEXIST";
            return -17;
        }
        if (!_files.ContainsKey(key) && !isCreate)
        {
            fh = IntPtr.Zero;
            CurrentError = "NFS4ERR_NOENT";
            return -2;
        }
        if (isTrunc || !_files.ContainsKey(key))
        {
            _files[key] = [];
            _mtimes[key] = DateTime.UtcNow;
            EnsureParentDirsKey(key);
        }

        FileHandle handle = new() { Path = key, Mode = flags };
        IntPtr ptr = new(Interlocked.Increment(ref _nextHandle));
        _openHandles[ptr] = handle;
        fh = ptr;
        return 0;
    }

    public int Creat(IntPtr nfs, string path, int mode, out IntPtr fh)
    {
        if (TryFault(nameof(Creat), out int rc, out _))
        {
            fh = IntPtr.Zero;
            return rc;
        }
        string key = Normalise(path);
        _files[key] = [];
        _mtimes[key] = DateTime.UtcNow;
        EnsureParentDirsKey(key);
        FileHandle handle = new() { Path = key };
        IntPtr ptr = new(Interlocked.Increment(ref _nextHandle));
        _openHandles[ptr] = handle;
        fh = ptr;
        return 0;
    }

    public int Close(IntPtr nfs, IntPtr fh)
    {
        TryFault(nameof(Close), out _, out _);
        _openHandles.TryRemove(fh, out _);
        return 0;
    }

    public int Read(IntPtr nfs, IntPtr fh, IntPtr buf, int count)
    {
        if (TryFault(nameof(Read), out int rc, out _))
            return rc;

        if (!_openHandles.TryGetValue(fh, out FileHandle? handle))
            return -9;
        if (!_files.TryGetValue(handle.Path, out byte[]? content))
            return -2;

        long remaining = content.Length - handle.Position;
        int toRead = (int)Math.Min(count, remaining);
        if (toRead <= 0)
            return 0;
        Marshal.Copy(content, (int)handle.Position, buf, toRead);
        handle.Position += toRead;
        return toRead;
    }

    public int Write(IntPtr nfs, IntPtr fh, IntPtr buf, int count)
    {
        if (TryFault(nameof(Write), out int rc, out _))
            return rc;

        if (!_openHandles.TryGetValue(fh, out FileHandle? handle))
            return -9;
        if (!_files.TryGetValue(handle.Path, out byte[]? content))
            return -2;

        byte[] writeBuf = new byte[count];
        Marshal.Copy(buf, writeBuf, 0, count);

        long newSize = handle.Position + count;
        if (newSize > content.Length)
            Array.Resize(ref content, (int)newSize);
        Array.Copy(writeBuf, 0, content, handle.Position, count);
        _files[handle.Path] = content;
        _mtimes[handle.Path] = DateTime.UtcNow;
        handle.Position += count;
        return count;
    }

    public long Lseek(IntPtr nfs, IntPtr fh, long offset, int whence, out ulong currentOffset)
    {
        currentOffset = 0;
        if (TryFault(nameof(Lseek), out int rc, out _))
            return rc;
        if (!_openHandles.TryGetValue(fh, out FileHandle? handle))
            return -9;
        handle.Position = offset;
        currentOffset = (ulong)offset;
        return offset;
    }

    public int Unlink(IntPtr nfs, string path)
    {
        if (TryFault(nameof(Unlink), out int rc, out _))
            return rc;
        _files.TryRemove(Normalise(path), out _);
        return 0;
    }

    public int Rename(IntPtr nfs, string oldPath, string newPath)
    {
        if (TryFault(nameof(Rename), out int rc, out _))
            return rc;
        string from = Normalise(oldPath);
        string to = Normalise(newPath);
        if (_files.TryRemove(from, out byte[]? content))
        {
            _files[to] = content;
            return 0;
        }
        if (_dirs.TryRemove(from, out _))
        {
            _dirs[to] = true;
            return 0;
        }
        CurrentError = "NFS4ERR_NOENT";
        return -2;
    }

    public int Readlink(IntPtr nfs, string path, IntPtr buf, int bufSize)
    {
        if (TryFault(nameof(Readlink), out int rc, out _))
            return rc;
        return -1;
    }

    public IntPtr MountGetExports(string server) => IntPtr.Zero;

    public void MountFreeExportList(IntPtr exports) { }
}
