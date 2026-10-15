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
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace NoMercy.Storage.Drivers.Local;

/// <summary>
/// Marks a file sparse on NTFS.
/// <para>
/// Without the flag, NTFS reserves a file's whole length the moment it is
/// set, and every write past the valid data length zero-fills the gap first.
/// A download written in the order its pieces arrive then takes about twice
/// as long, and single writes stall for seconds. .NET has no managed way to
/// set the flag: <c>File.SetAttributes</c> with <c>SparseFile</c> does not.
/// </para>
/// <para>
/// Every other file system the server runs on is sparse already, so off
/// Windows this does nothing. A file system that refuses the call, such as
/// FAT32 on a USB disk, answers false and the file stays as it was.
/// </para>
/// <para>
/// Only an existing file at a full local path is marked. A relative path
/// would resolve against whatever the working directory is, and a network
/// path is another machine's disk; both answer false rather than guess.
/// </para>
/// </summary>
internal static class SparseFile
{
    private const uint FsctlSetSparse = 0x000900C4;

    // Only the right to change attributes. It is not subject to sharing, so
    // the call works while the writer that created the file still holds it
    // open, which is exactly when it is needed: before the length is set.
    private const uint FileWriteAttributes = 0x00000100;

    private const uint OpenExisting = 3;

    public static bool TryMark(string fullPath)
    {
        if (!OperatingSystem.IsWindows() || !IsLocalFile(fullPath))
            return false;

        return TryMarkOnWindows(fullPath);
    }

    /// <summary>A full path on this machine that names a file, not a folder.</summary>
    internal static bool IsLocalFile(string path) =>
        !string.IsNullOrWhiteSpace(path)
        && Path.IsPathFullyQualified(path)
        // UNC and device paths: \\server\share, \\?\ and \\.\ all start this way.
        && !path.StartsWith(@"\\", StringComparison.Ordinal)
        && !path.StartsWith("//", StringComparison.Ordinal)
        && File.Exists(path);

    [SupportedOSPlatform("windows")]
    private static bool TryMarkOnWindows(string fullPath)
    {
        using SafeFileHandle handle = CreateFile(
            fullPath,
            FileWriteAttributes,
            FileShare.ReadWrite | FileShare.Delete,
            IntPtr.Zero,
            OpenExisting,
            0,
            IntPtr.Zero
        );

        if (handle.IsInvalid)
            return false;

        return DeviceIoControl(
            handle,
            FsctlSetSparse,
            IntPtr.Zero,
            0,
            IntPtr.Zero,
            0,
            out _,
            IntPtr.Zero
        );
    }

    [SupportedOSPlatform("windows")]
    [DllImport(
        "kernel32.dll",
        EntryPoint = "CreateFileW",
        CharSet = CharSet.Unicode,
        SetLastError = true
    )]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        FileShare shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile
    );

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(
        SafeFileHandle device,
        uint ioControlCode,
        IntPtr inBuffer,
        uint inBufferSize,
        IntPtr outBuffer,
        uint outBufferSize,
        out uint bytesReturned,
        IntPtr overlapped
    );
}
