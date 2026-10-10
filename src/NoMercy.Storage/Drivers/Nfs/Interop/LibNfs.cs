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

using System.Runtime.InteropServices;

namespace NoMercy.Storage.Drivers.Nfs.Interop;

/// <summary>
/// P/Invoke bindings for libnfs (https://github.com/sahlberg/libnfs).
/// MIT-licensed C library — cross-platform NFS3/NFS4 client without OS mount.
///
/// Native binary locations (resolved by .NET runtime DLL search):
///   Windows  → runtimes/win-x64/native/libnfs.dll
///   Linux    → runtimes/linux-x64/native/libnfs.so
///            → runtimes/linux-arm64/native/libnfs.so
///   macOS    → runtimes/osx-x64/native/libnfs.dylib
///            → runtimes/osx-arm64/native/libnfs.dylib
///
/// All <c>string</c> parameters are marshalled as UTF-8 (LPUTF8Str). NFS3/4
/// transmits names as UTF-8 over the wire, and CharSet.Ansi would lose
/// non-ASCII characters under Windows CP-1252 (e.g. "Käärijä", "東京").
///
/// For development without the NuGet package, install the system library:
///   Linux:   apt install libnfs-dev  (or dnf/pacman equivalent)
///   macOS:   brew install libnfs
///   Windows: pre-built DLLs from the libnfs GitHub releases page
/// </summary>
internal static class LibNfs
{
    private const string LibName = "nfs";

    /// <summary>
    /// libnfs lives under <c>runtimes/{rid}/native/</c> but is bundled via a
    /// plain <c>&lt;None&gt;</c> copy (not a NuGet runtime asset), so the
    /// default .NET native-library probe doesn't pick it up under
    /// <c>dotnet test</c>. Wire a resolver that probes the well-known
    /// per-RID folders next to the assembly before falling back to the
    /// default load mechanism (which still works for <c>dotnet run</c> when
    /// the OS happens to find the file on its own search path).
    /// </summary>
    static LibNfs()
    {
        NativeLibrary.SetDllImportResolver(
            typeof(LibNfs).Assembly,
            (name, asm, searchPath) =>
            {
                if (name != LibName)
                    return IntPtr.Zero;

                string assemblyDir =
                    Path.GetDirectoryName(asm.Location) ?? AppContext.BaseDirectory;
                string rid =
                    OperatingSystem.IsWindows() ? "win-x64"
                    : OperatingSystem.IsLinux()
                        ? (
                            RuntimeInformation.OSArchitecture == Architecture.Arm64
                                ? "linux-arm64"
                                : "linux-x64"
                        )
                    : OperatingSystem.IsMacOS()
                        ? (
                            RuntimeInformation.OSArchitecture == Architecture.Arm64
                                ? "osx-arm64"
                                : "osx-x64"
                        )
                    : "win-x64";

                string fileName =
                    OperatingSystem.IsWindows() ? "nfs.dll"
                    : OperatingSystem.IsMacOS() ? "libnfs.dylib"
                    : "libnfs.so";

                string[] candidates =
                [
                    Path.Combine(assemblyDir, "runtimes", rid, "native", fileName),
                    Path.Combine(assemblyDir, fileName),
                    Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native", fileName),
                    Path.Combine(AppContext.BaseDirectory, fileName),
                ];

                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out IntPtr h))
                        return h;
                }

                return IntPtr.Zero;
            }
        );
    }

    // -----------------------------------------------------------------------
    // Context lifecycle
    // -----------------------------------------------------------------------

    [DllImport(
        LibName,
        EntryPoint = "nfs_init_context",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern IntPtr InitContext();

    [DllImport(
        LibName,
        EntryPoint = "nfs_destroy_context",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void DestroyContext(IntPtr nfs);

    // -----------------------------------------------------------------------
    // Mount
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_mount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Mount(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string server,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string exportPath
    );

    [DllImport(LibName, EntryPoint = "nfs_umount", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Umount(IntPtr nfs);

    // -----------------------------------------------------------------------
    // Auth
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_set_uid", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetUid(IntPtr nfs, int uid);

    [DllImport(LibName, EntryPoint = "nfs_set_gid", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void SetGid(IntPtr nfs, int gid);

    /// <summary>
    /// Set the NFS protocol version (3 or 4). Must be called before
    /// <c>nfs_mount</c>. libnfs defaults to NFSv3 when this is not set.
    /// Returns 0 on success, negative on error.
    /// </summary>
    [DllImport(
        LibName,
        EntryPoint = "nfs_set_version",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern int SetVersion(IntPtr nfs, int version);

    /// <summary>
    /// Set the NFSv4 client name (the client-id string the server keys open-owner
    /// and lock-owner sequence state on). libnfs derives this from the hostname by
    /// default, so two contexts in the same process share one client id and their
    /// open-owner seqids collide (NFS4ERR_BAD_SEQID). Giving each context a unique
    /// name before mount makes multiple NFS drivers coexist. NFSv4 only — a no-op
    /// for v3. Must be called before <c>nfs_mount</c>.
    /// </summary>
    [DllImport(
        LibName,
        EntryPoint = "nfs4_set_client_name",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void SetClientName(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string id
    );

    /// <summary>
    /// Set the NFSv4 client verifier — paired with the client name to uniquely
    /// identify this client instance to the server. NFSv4 only.
    /// </summary>
    [DllImport(
        LibName,
        EntryPoint = "nfs4_set_verifier",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void SetVerifier(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string verifier
    );

    // -----------------------------------------------------------------------
    // Error
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_get_error", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr GetErrorPtr(IntPtr nfs);

    internal static string GetError(IntPtr nfs)
    {
        IntPtr ptr = GetErrorPtr(nfs);
        return ptr == IntPtr.Zero
            ? "unknown error"
            : Marshal.PtrToStringUTF8(ptr) ?? "unknown error";
    }

    // -----------------------------------------------------------------------
    // Stat / attribute
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_stat64", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Stat64(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out NfsStat64 stat
    );

    [DllImport(LibName, EntryPoint = "nfs_lstat64", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Lstat64(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out NfsStat64 stat
    );

    // -----------------------------------------------------------------------
    // Directory
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_opendir", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int OpenDir(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        out IntPtr dir
    );

    [DllImport(LibName, EntryPoint = "nfs_readdir", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr ReadDir(IntPtr nfs, IntPtr dir);

    [DllImport(LibName, EntryPoint = "nfs_closedir", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void CloseDir(IntPtr nfs, IntPtr dir);

    [DllImport(LibName, EntryPoint = "nfs_mkdir", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int MkDir(IntPtr nfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibName, EntryPoint = "nfs_rmdir", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int RmDir(IntPtr nfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    // -----------------------------------------------------------------------
    // File I/O
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_open", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Open(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        out IntPtr fh
    );

    [DllImport(LibName, EntryPoint = "nfs_creat", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Creat(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int mode,
        out IntPtr fh
    );

    [DllImport(LibName, EntryPoint = "nfs_close", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Close(IntPtr nfs, IntPtr fh);

    [DllImport(LibName, EntryPoint = "nfs_read", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Read(IntPtr nfs, IntPtr fh, IntPtr buf, int count);

    [DllImport(LibName, EntryPoint = "nfs_write", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Write(IntPtr nfs, IntPtr fh, IntPtr buf, int count);

    [DllImport(LibName, EntryPoint = "nfs_lseek", CallingConvention = CallingConvention.Cdecl)]
    internal static extern long Lseek(
        IntPtr nfs,
        IntPtr fh,
        long offset,
        int whence,
        out ulong currentOffset
    );

    [DllImport(LibName, EntryPoint = "nfs_unlink", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Unlink(IntPtr nfs, [MarshalAs(UnmanagedType.LPUTF8Str)] string path);

    [DllImport(LibName, EntryPoint = "nfs_rename", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Rename(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath
    );

    // -----------------------------------------------------------------------
    // Symlink
    // -----------------------------------------------------------------------

    [DllImport(LibName, EntryPoint = "nfs_readlink", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int Readlink(
        IntPtr nfs,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        IntPtr buf,
        int bufSize
    );

    // -----------------------------------------------------------------------
    // Mount-protocol export enumeration
    // -----------------------------------------------------------------------

    [DllImport(
        LibName,
        EntryPoint = "mount_getexports",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern IntPtr MountGetExports(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string server
    );

    [DllImport(
        LibName,
        EntryPoint = "mount_free_export_list",
        CallingConvention = CallingConvention.Cdecl
    )]
    internal static extern void MountFreeExportList(IntPtr exports);

    [StructLayout(LayoutKind.Sequential)]
    internal struct ExportEntry
    {
        // Field order MUST match the ONC RPC MOUNT `exportnode` (RFC 1813): the
        // directory path first, then the group list, then the next node. The
        // previous order (ex_next first) misaligned every pointer, so the moment
        // a server actually answered the v3 MOUNT protocol the export walk read
        // the group pointer as the path and dereferenced garbage.
        public IntPtr ExDir;
        public IntPtr ExGroups;
        public IntPtr ExNext;
    }

    // -----------------------------------------------------------------------
    // Native open flags; libnfs interprets these using its build platform's fcntl.h.
    // -----------------------------------------------------------------------

    internal const int O_RDONLY = 0;
    internal const int O_WRONLY = 1;
    internal const int O_RDWR = 2;
    internal static readonly int O_CREAT =
        OperatingSystem.IsWindows() ? 0x100
        : OperatingSystem.IsMacOS() ? 0x200
        : 0x40;
    internal static readonly int O_TRUNC = OperatingSystem.IsMacOS() ? 0x400 : 0x200;
    internal static readonly int O_EXCL =
        OperatingSystem.IsWindows() ? 0x400
        : OperatingSystem.IsMacOS() ? 0x800
        : 0x80;

    internal const int DefaultFileMode = 0x1A4; // 0644

    // -----------------------------------------------------------------------
    // NFS3 ftype3 constants — used by `nfsdirent.type` (libnfs translates v4
    // entries to these values). DO NOT use against the POSIX `mode` field of
    // `nfs_stat_64` — that uses the values below.
    // -----------------------------------------------------------------------

    internal const int NF3REG = 1;
    internal const int NF3DIR = 2;
    internal const int NF3LNK = 5;

    // -----------------------------------------------------------------------
    // POSIX file-type bits — extracted from `nfs_stat_64.mode` via `(mode >>
    // 12) & 0xF`. Different convention from NFS3 ftype3 above.
    // -----------------------------------------------------------------------

    internal const int S_IFREG = 0x8; // regular file   (0o100000 >> 12)
    internal const int S_IFDIR = 0x4; // directory      (0o040000 >> 12)
    internal const int S_IFLNK = 0xA; // symbolic link  (0o120000 >> 12)

    // -----------------------------------------------------------------------
    // Structs
    // -----------------------------------------------------------------------

    /// <summary>
    /// libnfs <c>struct nfs_stat_64</c> from <c>libnfs.h</c>. All-uint64
    /// layout, identical across Linux / macOS / Windows because libnfs
    /// declares the fields explicitly as <c>uint64_t</c>. Note the field
    /// order: the three Sec values come first, then the three Nsec values,
    /// then <c>used</c> at the end (NOT interleaved).
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NfsStat64
    {
        public ulong DevId;
        public ulong Ino;
        public ulong Mode;
        public ulong Nlink;
        public ulong Uid;
        public ulong Gid;
        public ulong Rdev;
        public ulong Size;
        public ulong BlkSize;
        public ulong Blocks;
        public ulong AtimeSec;
        public ulong MtimeSec;
        public ulong CtimeSec;
        public ulong AtimeNsec;
        public ulong MtimeNsec;
        public ulong CtimeNsec;
        public ulong Used;

        /// <summary>
        /// File type bits extracted from POSIX mode (top 4 bits, shifted
        /// right 12). Compare against <see cref="S_IFREG"/>,
        /// <see cref="S_IFDIR"/>, <see cref="S_IFLNK"/>.
        /// </summary>
        internal int FileType => (int)((Mode >> 12) & 0xF);
    }

    /// <summary>
    /// libnfs <c>struct nfsdirent</c> as declared in <c>libnfs.h</c>. Only the
    /// first four fields (next, name, inode, type) are needed by our driver;
    /// the remaining fields (mode/size/timeval triples/uid/gid/...) are left
    /// out because <c>struct timeval</c> width differs between Linux (long=8)
    /// and MinGW Windows (long=4), making the tail of the struct platform-
    /// dependent. Reading only up to <c>type</c> (offset 28) is portable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct NfsDirent
    {
        public IntPtr Next;
        public IntPtr Name;
        public ulong Inode;
        public uint Type;
    }

    /// <summary>
    /// Read the entry name from libnfs's <c>nfsdirent.name</c> char-pointer.
    /// libnfs owns the memory; we only marshal a copy. NFS3/NFS4 use UTF-8
    /// over the wire, so we decode as UTF-8 (not ANSI / CP-1252) to keep
    /// non-ASCII filenames intact (e.g. "Käärijä", "東京").
    /// </summary>
    internal static string? ReadDirentName(NfsDirent entry) =>
        entry.Name == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(entry.Name);
}
