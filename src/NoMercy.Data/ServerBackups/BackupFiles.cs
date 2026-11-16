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

using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using NoMercy.Storage;

namespace NoMercy.Data.ServerBackups;

/// <summary>
/// File helpers shared by backup and restore. Every touch of the disk goes
/// through the <see cref="IStorageDriver"/> passed in.
/// </summary>
public static class BackupFiles
{
    private static readonly JsonSerializerSettings ManifestSettings = new()
    {
        Formatting = Formatting.Indented,
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
        MissingMemberHandling = MissingMemberHandling.Ignore,
    };

    /// <summary>Lowercase hex SHA-256 of a file.</summary>
    public static async Task<string> Sha256Async(
        IStorageDriver storage,
        string path,
        CancellationToken cancellationToken
    )
    {
        await using Stream stream = storage.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>
    /// Writes text to a temporary file next to <paramref name="path"/>, flushes
    /// it to disk, then renames it into place, so a crash leaves either the old
    /// file or the new one, never half of one.
    /// </summary>
    public static async Task WriteTextAtomicallyAsync(
        IStorageDriver storage,
        string path,
        string contents,
        CancellationToken cancellationToken
    )
    {
        string temporary = path + ".tmp";
        if (storage.FileExists(temporary))
            storage.DeleteFile(temporary);

        await using (Stream stream = storage.OpenWrite(temporary, overwrite: false))
        {
            byte[] bytes = Encoding.UTF8.GetBytes(contents);
            await stream.WriteAsync(bytes, cancellationToken);
            if (stream is FileStream fileStream)
                fileStream.Flush(flushToDisk: true);
            else
                await stream.FlushAsync(cancellationToken);
        }

        if (storage.FileExists(path))
            storage.DeleteFile(path);
        storage.MoveFile(temporary, path);
    }

    public static async Task<string> ReadTextAsync(
        IStorageDriver storage,
        string path,
        CancellationToken cancellationToken
    )
    {
        await using Stream stream = storage.OpenRead(path);
        using StreamReader reader = new(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public static string Serialize<T>(T value) =>
        JsonConvert.SerializeObject(value, ManifestSettings);

    /// <summary>Null when the text is not valid JSON for <typeparamref name="T"/>.</summary>
    public static T? Deserialize<T>(string json)
        where T : class
    {
        try
        {
            return JsonConvert.DeserializeObject<T>(json, ManifestSettings);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every file under <paramref name="root"/>, as paths relative to it with
    /// forward slashes, in ordinal order. Empty when the folder does not exist.
    /// </summary>
    public static List<string> ListFilesRelative(IStorageDriver storage, string root) =>
        storage.DirectoryExists(root)
            ? storage
                .EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)
                .Where(storage.FileExists)
                .Select(path =>
                    Path.GetRelativePath(root, path).Replace(Path.DirectorySeparatorChar, '/')
                )
                .Order(StringComparer.Ordinal)
                .ToList()
            : [];

    /// <summary>
    /// Joins a set-relative forward-slash path onto a folder, refusing any path
    /// that would leave it (a crafted manifest must not reach outside the set).
    /// </summary>
    public static string ResolveInside(string root, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || Path.IsPathRooted(relativePath))
            throw new InvalidDataException($"Backup path '{relativePath}' is not relative.");

        string fullRoot =
            Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(
            Path.Combine(fullRoot, relativePath.Replace('/', Path.DirectorySeparatorChar))
        );
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(fullRoot, comparison))
            throw new InvalidDataException($"Backup path '{relativePath}' leaves its folder.");

        return fullPath;
    }
}
