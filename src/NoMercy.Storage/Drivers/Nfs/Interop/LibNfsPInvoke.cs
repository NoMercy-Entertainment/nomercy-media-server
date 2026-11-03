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
/// Production <see cref="ILibNfs"/> implementation. Forwards each call
/// straight through to the corresponding static <see cref="LibNfs"/> P/Invoke
/// wrapper — no behaviour change, just a virtual dispatch seam so tests can
/// substitute a fault-injecting fake.
/// </summary>
internal sealed class LibNfsPInvoke : ILibNfs
{
    public static readonly LibNfsPInvoke Instance = new();

    public IntPtr InitContext() => LibNfs.InitContext();

    public void DestroyContext(IntPtr nfs) => LibNfs.DestroyContext(nfs);

    public int Mount(IntPtr nfs, string server, string exportPath) =>
        LibNfs.Mount(nfs, server, exportPath);

    public int Umount(IntPtr nfs) => LibNfs.Umount(nfs);

    public void SetUid(IntPtr nfs, int uid) => LibNfs.SetUid(nfs, uid);

    public void SetGid(IntPtr nfs, int gid) => LibNfs.SetGid(nfs, gid);

    public int SetVersion(IntPtr nfs, int version) => LibNfs.SetVersion(nfs, version);

    public void SetClientName(IntPtr nfs, string id) => LibNfs.SetClientName(nfs, id);

    public void SetVerifier(IntPtr nfs, string verifier) => LibNfs.SetVerifier(nfs, verifier);

    public string GetError(IntPtr nfs) => LibNfs.GetError(nfs);

    public int Stat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat) =>
        LibNfs.Stat64(nfs, path, out stat);

    public int Lstat64(IntPtr nfs, string path, out LibNfs.NfsStat64 stat) =>
        LibNfs.Lstat64(nfs, path, out stat);

    public int OpenDir(IntPtr nfs, string path, out IntPtr dir) =>
        LibNfs.OpenDir(nfs, path, out dir);

    public IntPtr ReadDir(IntPtr nfs, IntPtr dir) => LibNfs.ReadDir(nfs, dir);

    public void CloseDir(IntPtr nfs, IntPtr dir) => LibNfs.CloseDir(nfs, dir);

    public int MkDir(IntPtr nfs, string path) => LibNfs.MkDir(nfs, path);

    public int RmDir(IntPtr nfs, string path) => LibNfs.RmDir(nfs, path);

    public int Open(IntPtr nfs, string path, int flags, out IntPtr fh) =>
        LibNfs.Open(nfs, path, flags, out fh);

    public int Creat(IntPtr nfs, string path, int mode, out IntPtr fh) =>
        LibNfs.Creat(nfs, path, mode, out fh);

    public int Close(IntPtr nfs, IntPtr fh) => LibNfs.Close(nfs, fh);

    public int Read(IntPtr nfs, IntPtr fh, IntPtr buf, int count) =>
        LibNfs.Read(nfs, fh, buf, count);

    public int Write(IntPtr nfs, IntPtr fh, IntPtr buf, int count) =>
        LibNfs.Write(nfs, fh, buf, count);

    public int Lseek(IntPtr nfs, IntPtr fh, long offset, int whence, out ulong currentOffset) =>
        LibNfs.Lseek(nfs, fh, offset, whence, out currentOffset);

    public int Unlink(IntPtr nfs, string path) => LibNfs.Unlink(nfs, path);

    public int Rename(IntPtr nfs, string oldPath, string newPath) =>
        LibNfs.Rename(nfs, oldPath, newPath);

    public int Readlink(IntPtr nfs, string path, IntPtr buf, int bufSize) =>
        LibNfs.Readlink(nfs, path, buf, bufSize);

    public IntPtr MountGetExports(string server) => LibNfs.MountGetExports(server);

    public void MountFreeExportList(IntPtr exports) => LibNfs.MountFreeExportList(exports);
}
