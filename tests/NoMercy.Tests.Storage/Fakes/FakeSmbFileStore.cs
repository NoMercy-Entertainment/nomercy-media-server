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

using System.Text.RegularExpressions;
using SMBLibrary;
using SMBLibrary.Client;
using FileAttributes = SMBLibrary.FileAttributes;

namespace NoMercy.Tests.Storage.Fakes;

/// <summary>
/// In-memory <see cref="ISMBFileStore"/> standing in for an SMB server during
/// directory enumeration. It answers QUERY_DIRECTORY the way a server does:
/// the search pattern is applied to every entry name, folders included, a
/// query nothing matches returns STATUS_NO_SUCH_FILE, and an exhausted
/// listing ends with STATUS_NO_MORE_FILES (what SMB2FileStore hands back once
/// it has drained every page). Only the members enumeration touches are
/// modelled; everything else throws so a test cannot pass by accident.
/// </summary>
internal sealed class FakeSmbFileStore : ISMBFileStore
{
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase)
    {
        string.Empty,
    };
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);

    public uint MaxReadSize => 1024 * 1024;

    public uint MaxWriteSize => 1024 * 1024;

    public void SeedDirectory(string path)
    {
        string key = Key(path);
        while (key.Length > 0)
        {
            _directories.Add(key);
            key = ParentOf(key);
        }
    }

    public void SeedFile(string path)
    {
        string key = Key(path);
        _files.Add(key);
        SeedDirectory(ParentOf(key));
    }

    public NTStatus CreateFile(
        out object handle,
        out FileStatus fileStatus,
        string path,
        AccessMask desiredAccess,
        FileAttributes fileAttributes,
        ShareAccess shareAccess,
        CreateDisposition createDisposition,
        CreateOptions createOptions,
        SecurityContext securityContext
    )
    {
        string key = Key(path);
        handle = key;

        bool exists = createOptions.HasFlag(CreateOptions.FILE_DIRECTORY_FILE)
            ? _directories.Contains(key)
            : _files.Contains(key);
        if (!exists)
        {
            fileStatus = FileStatus.FILE_DOES_NOT_EXIST;
            return NTStatus.STATUS_OBJECT_NAME_NOT_FOUND;
        }

        fileStatus = FileStatus.FILE_OPENED;
        return NTStatus.STATUS_SUCCESS;
    }

    public NTStatus CloseFile(object handle) => NTStatus.STATUS_SUCCESS;

    public NTStatus QueryDirectory(
        out List<QueryDirectoryFileInformation> result,
        object handle,
        string fileName,
        FileInformationClass informationClass
    )
    {
        string directory = (string)handle;
        Regex matcher = ToRegex(fileName);

        List<(string Name, bool IsDirectory)> children =
        [
            (".", true),
            ("..", true),
            .. _directories
                .Where(d => d.Length > 0 && IsParent(directory, d))
                .Select(d => (NameOf(d), true)),
            .. _files.Where(f => IsParent(directory, f)).Select(f => (NameOf(f), false)),
        ];

        result = [];
        foreach ((string name, bool isDirectory) in children)
        {
            if (!matcher.IsMatch(name))
                continue;

            result.Add(
                new FileDirectoryInformation
                {
                    FileName = name,
                    FileAttributes = isDirectory ? FileAttributes.Directory : FileAttributes.Normal,
                }
            );
        }

        return result.Count == 0 ? NTStatus.STATUS_NO_SUCH_FILE : NTStatus.STATUS_NO_MORE_FILES;
    }

    public NTStatus Disconnect() => NTStatus.STATUS_SUCCESS;

    public NTStatus ReadFile(out byte[] data, object handle, long offset, int maxCount) =>
        throw NotModelled();

    public NTStatus WriteFile(
        out int numberOfBytesWritten,
        object handle,
        long offset,
        byte[] data
    ) => throw NotModelled();

    public NTStatus FlushFileBuffers(object handle) => throw NotModelled();

    public NTStatus LockFile(object handle, long byteOffset, long length, bool exclusiveLock) =>
        throw NotModelled();

    public NTStatus UnlockFile(object handle, long byteOffset, long length) => throw NotModelled();

    public NTStatus GetFileInformation(
        out FileInformation result,
        object handle,
        FileInformationClass informationClass
    ) => throw NotModelled();

    public NTStatus SetFileInformation(object handle, FileInformation information) =>
        throw NotModelled();

    public NTStatus GetFileSystemInformation(
        out FileSystemInformation result,
        FileSystemInformationClass informationClass
    ) => throw NotModelled();

    public NTStatus SetFileSystemInformation(FileSystemInformation information) =>
        throw NotModelled();

    public NTStatus GetSecurityInformation(
        out SecurityDescriptor result,
        object handle,
        SecurityInformation securityInformation
    ) => throw NotModelled();

    public NTStatus SetSecurityInformation(
        object handle,
        SecurityInformation securityInformation,
        SecurityDescriptor securityDescriptor
    ) => throw NotModelled();

    public NTStatus NotifyChange(
        out object ioRequest,
        object handle,
        NotifyChangeFilter completionFilter,
        bool watchTree,
        int outputBufferSize,
        OnNotifyChangeCompleted onNotifyChangeCompleted,
        object context
    ) => throw NotModelled();

    public NTStatus Cancel(object ioRequest) => throw NotModelled();

    public NTStatus DeviceIOControl(
        object handle,
        uint ctlCode,
        byte[] input,
        out byte[] output,
        int maxOutputLength
    ) => throw NotModelled();

    private static NotSupportedException NotModelled() =>
        new("FakeSmbFileStore only models directory enumeration.");

    private static string Key(string path) => path.Replace('\\', '/').Trim('/');

    private static string ParentOf(string key)
    {
        int slash = key.LastIndexOf('/');
        return slash < 0 ? string.Empty : key[..slash];
    }

    private static string NameOf(string key) => key[(key.LastIndexOf('/') + 1)..];

    private static bool IsParent(string directory, string key) =>
        string.Equals(ParentOf(key), directory, StringComparison.OrdinalIgnoreCase);

    // SMB wildcards: '*' any run, '?' one character, names compared without case.
    private static Regex ToRegex(string pattern) =>
        new(
            "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase
        );
}
