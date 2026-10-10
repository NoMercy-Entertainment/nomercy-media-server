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

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using NoMercy.Storage.Validation;

namespace NoMercy.Storage.Drivers.Local;

/// <summary>
/// Local-disk implementation of <see cref="IStorage"/>. Every path is
/// validated through <see cref="StoragePathGuard"/> in the constructor
/// before reaching <see cref="IStorageDriver"/>. Stream-returning
/// methods hand out <see cref="FileStream"/> objects with
/// <c>useAsync: true</c> so callers can await reads/writes naturally.
/// </summary>
public sealed class LocalStorage : IStorage
{
    private readonly IStorageDriver _driver;
    private readonly StoragePathGuard _guard;

    public LocalStorage(IStorageDriver driver, StoragePathGuard guard)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _guard = guard ?? throw new ArgumentNullException(nameof(guard));
    }

    public IStorageDriver Driver => _driver;

    public async Task<byte[]> ReadAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        await using Stream stream = _driver.OpenRead(safe);
        using MemoryStream ms = new();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    public Task<Stream> OpenReadAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        return Task.FromResult(_driver.OpenRead(safe));
    }

    public async Task WriteAsync(string path, byte[] bytes, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        EnsureParentDirectory(safe);
        await using Stream stream = _driver.OpenWrite(safe, overwrite: true);
        await stream.WriteAsync(bytes.AsMemory(), ct);
    }

    public Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        EnsureParentDirectory(safe);
        return Task.FromResult(_driver.OpenWrite(safe, overwrite));
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        return Task.FromResult(_driver.FileExists(safe) || _driver.DirectoryExists(safe));
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        if (_driver.FileExists(safe))
            _driver.DeleteFile(safe);
        return Task.CompletedTask;
    }

    public Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        if (_driver.DirectoryExists(safe))
            _driver.DeleteDirectory(safe, recursive);
        return Task.CompletedTask;
    }

    public Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        _driver.CreateDirectory(safe);
        return Task.CompletedTask;
    }

    public Task MoveAsync(string from, string to, CancellationToken ct)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.MoveFile(safeFrom, safeTo);
        return Task.CompletedTask;
    }

    public Task CopyAsync(string from, string to, CancellationToken ct)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.CopyFile(safeFrom, safeTo, overwrite: true);
        return Task.CompletedTask;
    }

    public Task<long> SizeAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        return Task.FromResult(_driver.GetFileSize(safe));
    }

    public Task<DateTimeOffset> LastModifiedAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        DateTime utc = _driver.GetLastWriteTimeUtc(safe);
        return Task.FromResult(new DateTimeOffset(utc, TimeSpan.Zero));
    }

    public async IAsyncEnumerable<StorageEntry> ListAsync(
        string path,
        string? pattern,
        bool recursive,
        [EnumeratorCancellation] CancellationToken ct
    )
    {
        string safe = ValidateScoped(path);
        SearchOption option = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        string effectivePattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;

        foreach (StorageEntryInfo info in _driver.EnumerateEntries(safe, effectivePattern, option))
        {
            ct.ThrowIfCancellationRequested();
            yield return new(
                ToScopeRelative(info.Path),
                info.IsDirectory,
                info.Size,
                new(info.LastWriteUtc, TimeSpan.Zero)
            );
            await Task.Yield();
        }
    }

    public async Task<string> HashAsync(string path, string algorithm, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        using HashAlgorithm hasher = algorithm.ToLowerInvariant() switch
        {
            "sha256" => SHA256.Create(),
            "md5" => MD5.Create(),
            _ => throw new ArgumentException(
                $"unsupported hash algorithm: {algorithm} (allowed: sha256, md5)",
                nameof(algorithm)
            ),
        };

        await using Stream stream = _driver.OpenRead(safe);
        byte[] digest = await hasher.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    public Task<LocalPathLease> AcquireLocalPathAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        return Task.FromResult(new LocalPathLease(safe));
    }

    // --- Sync companions ----------------------------------------------------

    public bool Exists(string path)
    {
        string safe = ValidateScoped(path);
        return _driver.FileExists(safe) || _driver.DirectoryExists(safe);
    }

    public long SizeOrZero(string path)
    {
        string safe = ValidateScoped(path);
        return _driver.FileExists(safe) ? _driver.GetFileSize(safe) : 0L;
    }

    public long Size(string path)
    {
        string safe = ValidateScoped(path);
        return _driver.GetFileSize(safe);
    }

    public DateTimeOffset LastModified(string path)
    {
        string safe = ValidateScoped(path);
        return new(_driver.GetLastWriteTimeUtc(safe), TimeSpan.Zero);
    }

    public void CreateDirectory(string path)
    {
        string safe = ValidateScoped(path);
        _driver.CreateDirectory(safe);
    }

    public void Delete(string path)
    {
        string safe = ValidateScoped(path);
        if (_driver.FileExists(safe))
            _driver.DeleteFile(safe);
    }

    public void DeleteDirectory(string path, bool recursive)
    {
        string safe = ValidateScoped(path);
        if (_driver.DirectoryExists(safe))
            _driver.DeleteDirectory(safe, recursive);
    }

    public byte[] Read(string path)
    {
        string safe = ValidateScoped(path);
        using Stream stream = _driver.OpenRead(safe);
        using MemoryStream ms = new();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    public Stream OpenRead(string path)
    {
        string safe = ValidateScoped(path);
        return _driver.OpenRead(safe);
    }

    public Stream OpenWrite(string path, bool overwrite)
    {
        string safe = ValidateScoped(path);
        EnsureParentDirectory(safe);
        return _driver.OpenWrite(safe, overwrite);
    }

    public void Write(string path, byte[] bytes)
    {
        string safe = ValidateScoped(path);
        EnsureParentDirectory(safe);
        using Stream stream = _driver.OpenWrite(safe, overwrite: true);
        stream.Write(bytes, 0, bytes.Length);
    }

    public void Move(string from, string to)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.MoveFile(safeFrom, safeTo);
    }

    public void Copy(string from, string to)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.CopyFile(safeFrom, safeTo, overwrite: true);
    }

    public IReadOnlyList<StorageEntry> List(string path, string? pattern, bool recursive)
    {
        string safe = ValidateScoped(path);
        SearchOption option = recursive
            ? SearchOption.AllDirectories
            : SearchOption.TopDirectoryOnly;
        string effectivePattern = string.IsNullOrEmpty(pattern) ? "*" : pattern;

        List<StorageEntry> entries = [];
        // Single-pass metadata via the driver's readdir enumeration — size,
        // last-write and is-directory ride in on the OS directory listing, the
        // same path ListAsync already takes. The previous
        // EnumerateFileSystemEntries loop issued three extra stat calls per entry
        // (DirectoryExists + GetFileSize + GetLastWriteTimeUtc); over a network
        // mount that turned one listing of a rendition dir into hundreds of
        // round-trips, and a library scan lists every rendition dir of every file.
        foreach (StorageEntryInfo info in _driver.EnumerateEntries(safe, effectivePattern, option))
            entries.Add(
                new(
                    ToScopeRelative(info.Path),
                    info.IsDirectory,
                    info.Size,
                    new(info.LastWriteUtc, TimeSpan.Zero)
                )
            );
        return entries;
    }

    public LocalPathLease AcquireLocalPath(string path)
    {
        string safe = ValidateScoped(path);
        return new(safe);
    }

    string IStorage.GetFullPath(string path) => ValidateScoped(path);

    private void EnsureParentDirectory(string path)
    {
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent))
            return;
        if (!_driver.DirectoryExists(parent))
            _driver.CreateDirectory(parent);
    }

    /// <summary>
    /// Resolves a possibly-relative browse path against the storage's scoped
    /// root. Empty path → the root itself; relative path → root + path joined.
    /// Already-absolute paths pass through unchanged so callers that pass
    /// fully-qualified paths still work. Without this resolution, relative
    /// sub-paths canonicalize against the process CWD via Path.GetFullPath
    /// and fail the under-root guard check.
    ///
    /// Rootedness is checked with <see cref="StoragePathGuard.IsRootedAnyStyle"/>,
    /// not the OS-native <see cref="Path.IsPathRooted"/>: on Linux the latter
    /// returns false for a Windows drive-letter or UNC path, which would
    /// otherwise fall into the "relative" branch below and get silently
    /// joined under root — passing the under-root check for a path that was
    /// never actually inside it.
    /// </summary>
    private string ResolveAgainstScopedRoot(string path)
    {
        if (!_guard.Enforced || _guard.AllowedRoots.Count == 0)
            return path;

        string root = _guard.AllowedRoots[0];

        if (string.IsNullOrEmpty(path))
            return root;

        if (StoragePathGuard.IsRootedAnyStyle(path))
            return path;

        string normalized = path.Replace('\\', '/').TrimStart('/');
        return Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar));
    }

    /// <summary>
    /// Resolve-then-validate. Every IStorage entry point that takes a path
    /// must go through this wrapper instead of <c>_guard.Validate</c>
    /// directly, otherwise relative paths from callers (encoder file scanner,
    /// dashboard browser, anything outside the process CWD) trip the
    /// under-root guard check.
    /// </summary>
    private string ValidateScoped(string path) => _guard.Validate(ResolveAgainstScopedRoot(path));

    /// <summary>
    /// Strips the configured scoped root from an OS-absolute path returned
    /// by the driver's enumerate call, normalizes separators to forward
    /// slashes, and removes any leading slash. The result is scope-relative
    /// and suitable for passing back into any IStorage method (Rule 1 + 6).
    /// When no root is configured the path is only separator-normalized.
    /// </summary>
    private string ToScopeRelative(string absolutePath)
    {
        string normalized = absolutePath.Replace('\\', '/');

        if (!_guard.Enforced || _guard.AllowedRoots.Count == 0)
            return normalized;

        string root = _guard.AllowedRoots[0].Replace('\\', '/').TrimEnd('/');

        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (normalized.StartsWith(root + '/', comparison))
            return normalized[(root.Length + 1)..];

        if (string.Equals(normalized, root, comparison))
            return string.Empty;

        return normalized.TrimStart('/');
    }

    public async Task<string> ReadAllTextAsync(string path, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        using StreamReader reader = new(_driver.OpenRead(safe));
        return await reader.ReadToEndAsync(ct);
    }

    public async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        string safe = ValidateScoped(path);
        EnsureParentDirectory(safe);
        await using StreamWriter writer = new(_driver.OpenWrite(safe, overwrite: true));
        await writer.WriteAsync(contents.AsMemory(), ct);
        await writer.FlushAsync(ct);
    }

    public Task MoveDirectoryAsync(string from, string to, CancellationToken ct)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.MoveDirectory(safeFrom, safeTo);
        return Task.CompletedTask;
    }

    public void MoveDirectory(string from, string to)
    {
        string safeFrom = ValidateScoped(from);
        string safeTo = ValidateScoped(to);
        EnsureParentDirectory(safeTo);
        _driver.MoveDirectory(safeFrom, safeTo);
    }
}
