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

namespace NoMercy.Storage.Drivers.Nfs.Interop;

/// <summary>
/// Abstraction over libnfs P/Invoke. The production implementation
/// (<see cref="LibNfsPInvoke"/>) forwards every call to the static
/// <see cref="LibNfs"/> wrappers; tests use a fake that injects specific
/// rc/error-string combinations to deterministically exercise every
/// recovery path (NFS4ERR_EXPIRED, BAD_SEQID, BAD_STATEID, NOENT, EACCES, ...)
/// without needing a live server or Docker container.
///
/// One method per P/Invoke entry point — same names, same parameters, same
/// semantics. No higher-level abstractions; this is the seam for fault
/// injection, not for redesign.
/// </summary>
internal interface ILibNfs
{
    // Context lifecycle
    IntPtr InitContext();
    void DestroyContext(IntPtr nfs);

    // Mount
    int Mount(IntPtr nfs, string server, string exportPath);
    int Umount(IntPtr nfs);

    // Auth / version
    void SetUid(IntPtr nfs, int uid);
    void SetGid(IntPtr nfs, int gid);
    int SetVersion(IntPtr nfs, int version);

    // NFSv4 client identity — unique per context so multiple drivers in one
    // process don't share an open-owner seqid (NFS4ERR_BAD_SEQID).
    void SetClientName(IntPtr nfs, string id);
    void SetVerifier(IntPtr nfs, string verifier);

    // Error
    string GetError(IntPtr nfs);

    // Stat
    int Stat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat);
    int Lstat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat);

    // Directory
    int OpenDir(IntPtr nfs, string path, out IntPtr dir);
    IntPtr ReadDir(IntPtr nfs, IntPtr dir);
    void CloseDir(IntPtr nfs, IntPtr dir);
    int MkDir(IntPtr nfs, string path);
    int RmDir(IntPtr nfs, string path);

    // File I/O
    int Open(IntPtr nfs, string path, int flags, out IntPtr fh);
    int Creat(IntPtr nfs, string path, int mode, out IntPtr fh);
    int Close(IntPtr nfs, IntPtr fh);
    int Read(IntPtr nfs, IntPtr fh, IntPtr buf, int count);
    int Write(IntPtr nfs, IntPtr fh, IntPtr buf, int count);
    int Lseek(IntPtr nfs, IntPtr fh, long offset, int whence, out ulong currentOffset);
    int Unlink(IntPtr nfs, string path);
    int Rename(IntPtr nfs, string oldPath, string newPath);

    // Symlink
    int Readlink(IntPtr nfs, string path, IntPtr buf, int bufSize);

    // Mount-protocol export enumeration
    IntPtr MountGetExports(string server);
    void MountFreeExportList(IntPtr exports);
}
