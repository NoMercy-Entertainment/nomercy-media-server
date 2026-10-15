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

namespace NoMercy.Storage;

/// <summary>
/// Filesystem abstraction shared across the NoMercy codebase. A single
/// <see cref="Drivers.Local.LocalStorage"/> driver ships today; remote
/// drivers (NFS, S3, R2, WebDAV) plug in without touching consumer call sites.
/// </summary>
/// <remarks>
/// <para><strong>PATH CONTRACT (v1)</strong> — every IStorage method follows these rules:</para>
///
/// <para><strong>Rule 1 — PATHS ARE RELATIVE TO THE STORAGE'S SCOPE.</strong><br/>
/// The scope is whatever IStorageFactory configured: a local rootPath, an NFS export, an S3
/// bucket+prefix, a WebDAV base URL. Callers MUST NOT pass absolute backend paths.</para>
///
/// <para><strong>Rule 2 — SEPARATORS ARE FORWARD SLASHES.</strong><br/>
/// Backslashes are tolerated for legacy callers and normalized internally.
/// Mixed-separator strings from <c>System.IO.Path.Combine</c> on Windows are
/// normalized too, but producing them is an anti-pattern — use
/// <see cref="CombinePath"/>.</para>
///
/// <para><strong>Rule 3 — EMPTY OR NULL PATH = THE SCOPE ROOT.</strong><br/>
/// <c>List</c>/<c>Exists</c>/<c>Size</c> on <c>""</c> returns what's in the scoped root.
/// This is the natural browse-from-root semantic the dashboard relies on.
/// Note: <see cref="Validation.StoragePathGuard"/> currently rejects empty paths before
/// <see cref="Drivers.Local.LocalStorage.ResolveAgainstScopedRoot"/> can substitute the root —
/// see the architecture note in <see cref="Validation.StoragePathGuard"/> for the pending
/// resolution.</para>
///
/// <para><strong>Rule 4 — ABSOLUTE PATHS (rooted in OS terms) ARE REJECTED.</strong><br/>
/// A path already canonicalized to a backend-absolute form indicates the caller bypassed
/// the abstraction. <see cref="Validation.StoragePathGuard"/> surfaces this as
/// <see cref="Validation.StoragePathNotAllowedException"/>.</para>
///
/// <para><strong>Rule 5 — ".." TRAVERSAL AND NULL BYTES ARE REJECTED</strong> structurally
/// before any backend call. This is the security check that survives even when other rules
/// are relaxed.</para>
///
/// <para><strong>Rule 6 — THE DRIVER OWNS BACKEND TRANSLATION.</strong><br/>
/// <see cref="Drivers.Local.LocalStorage"/> joins with the OS root, NFS prepends the export,
/// S3 prepends bucket prefix, WebDAV prepends the base URL — all internal. Consumers never
/// see these prefixes.</para>
///
/// <para><strong>Important</strong>: <see cref="Remote.RemoteStorage"/> enforces Rules 4 and 5
/// via <see cref="Validation.StoragePathGuard.StructuralValidate"/> at every entry point.
/// The full under-root check (Rule 1) is not applicable to remote drivers because their
/// scope anchor is a URL/key prefix managed by the driver itself rather than an OS path.</para>
/// </remarks>
public interface IStorage
{
    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1) and use forward slashes
    /// (Rule 2). An empty string resolves to the scope root (Rule 3).
    /// </remarks>
    Task<byte[]> ReadAsync(string path, CancellationToken ct);

    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1). The returned stream is not
    /// buffered; callers own disposal.
    /// </remarks>
    Task<Stream> OpenReadAsync(string path, CancellationToken ct);

    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1). Parent directories are
    /// created automatically — callers do not call <see cref="CreateDirectoryAsync"/> first.
    /// </remarks>
    Task WriteAsync(string path, byte[] bytes, CancellationToken ct);

    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1). Caller owns stream disposal.
    /// </remarks>
    Task<Stream> OpenWriteAsync(string path, bool overwrite, CancellationToken ct);

    /// <remarks>
    /// Returns true for both files and directories. <paramref name="path"/> must be
    /// scope-relative (Rule 1). Empty path checks whether the scope root itself exists
    /// (Rule 3).
    /// </remarks>
    Task<bool> ExistsAsync(string path, CancellationToken ct);

    Task DeleteAsync(string path, CancellationToken ct);

    Task DeleteDirectoryAsync(string path, bool recursive, CancellationToken ct);

    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1). Intermediate directories are
    /// created recursively. Idempotent — calling on an existing directory is a no-op.
    /// </remarks>
    Task CreateDirectoryAsync(string path, CancellationToken ct);

    /// <remarks>
    /// Both <paramref name="from"/> and <paramref name="to"/> must be scope-relative (Rule 1).
    /// Crossing storage instances is not supported; use <see cref="CopyAsync"/> then
    /// <see cref="DeleteAsync"/> when the source and destination live on different
    /// <see cref="IStorage"/> instances.
    /// </remarks>
    Task MoveAsync(string from, string to, CancellationToken ct);

    /// <remarks>
    /// Both <paramref name="from"/> and <paramref name="to"/> must be scope-relative (Rule 1).
    /// </remarks>
    Task CopyAsync(string from, string to, CancellationToken ct);

    Task<long> SizeAsync(string path, CancellationToken ct);

    Task<DateTimeOffset> LastModifiedAsync(string path, CancellationToken ct);

    /// <remarks>
    /// <paramref name="path"/> must be scope-relative (Rule 1). An empty string lists
    /// the scope root (Rule 3). Each yielded <see cref="StorageEntry"/> path is also
    /// scope-relative — callers must not pass it to OS path APIs directly.
    /// </remarks>
    IAsyncEnumerable<StorageEntry> ListAsync(
        string path,
        string? pattern,
        bool recursive,
        CancellationToken ct
    );

    /// <summary>
    /// Compute a content hash. <paramref name="algorithm"/> is
    /// <c>sha256</c> or <c>md5</c> (case-insensitive). Returns a
    /// lowercase hex-encoded digest.
    /// </summary>
    Task<string> HashAsync(string path, string algorithm, CancellationToken ct);

    /// <summary>
    /// Acquire a real local path suitable for child-process consumption
    /// (ffmpeg, fpcalc, whisper, etc.). <see cref="Drivers.Local.LocalStorage"/>
    /// returns the validated path as-is with a no-op dispose; remote
    /// drivers stage to a temp file and clean up on dispose.
    /// </summary>
    /// <remarks>
    /// The path inside the returned <see cref="LocalPathLease"/> is a real OS-absolute path —
    /// it is intentionally NOT scope-relative. It must not be passed back into IStorage
    /// methods (that would violate Rule 1). Its sole purpose is child-process invocation.
    /// </remarks>
    Task<LocalPathLease> AcquireLocalPathAsync(string path, CancellationToken ct);

    // --- Sync companions ----------------------------------------------------
    //
    // The async surface above is the forward-compatible API — remote
    // drivers (SMB / NFS / S3 / R2) will do real I/O during these calls.
    // The sync companions below exist for call sites where the op is a
    // one-shot string/metadata check and async buys nothing. LocalStorage
    // executes them directly on the backend; remote drivers will block on
    // their own thread.

    bool Exists(string path);

    /// <summary>
    /// File size in bytes, or 0 when the file does not exist. Replaces
    /// the <c>(await Exists) ? await Size : 0</c> double-await pattern at
    /// call sites that only need the size for logging / reporting.
    /// </summary>
    long SizeOrZero(string path);

    long Size(string path);

    DateTimeOffset LastModified(string path);

    void CreateDirectory(string path);

    void Delete(string path);

    void DeleteDirectory(string path, bool recursive);

    byte[] Read(string path);

    Stream OpenRead(string path);

    Stream OpenWrite(string path, bool overwrite);

    void Write(string path, byte[] bytes);

    void Move(string from, string to);

    void Copy(string from, string to);

    /// <remarks>
    /// Each returned <see cref="StorageEntry"/> path is scope-relative (Rule 1).
    /// Do not pass these paths to <c>System.IO</c> APIs or
    /// <c>System.IO.Path.Combine</c> — use <see cref="CombinePath"/> if you
    /// need to build a child path from a listed entry (Rule 2).
    /// </remarks>
    IReadOnlyList<StorageEntry> List(string path, string? pattern, bool recursive);

    LocalPathLease AcquireLocalPath(string path);

    Task<string> ReadAllTextAsync(string path, CancellationToken ct);

    Task WriteAllTextAsync(string path, string contents, CancellationToken ct);

    Task MoveDirectoryAsync(string from, string to, CancellationToken ct);

    void MoveDirectory(string from, string to);

    /// <summary>
    /// The underlying low-level driver. Used by consumers (e.g. MediaScan)
    /// that accept an <see cref="IStorageDriver"/> directly.
    /// </summary>
    IStorageDriver Driver { get; }

    /// <summary>
    /// Delegates to <see cref="IStorageDriver.TryGetPresignedUrlAsync"/>.
    /// Returns null for local / NFS / WebDAV backends; S3/R2 return a
    /// time-limited URL the client can hit directly.
    /// </summary>
    Task<Uri?> TryGetPresignedUrlAsync(string path, TimeSpan ttl, CancellationToken ct) =>
        Driver.TryGetPresignedUrlAsync(path, ttl, ct);

    /// <summary>
    /// Marks an existing file sparse. Best effort: false for every backend
    /// but a local disk, and for a file system that refuses. See
    /// <see cref="IStorageDriver.TryMarkSparse"/>.
    /// </summary>
    bool TryMarkSparse(string path) => false;

    /// <summary>Driver's path separator. Convenience pass-through.</summary>
    char DirectorySeparator => Driver.DirectorySeparator;

    /// <summary>
    /// Driver-aware path join. Use this instead of
    /// <c>System.IO.Path.Combine</c> for any path that will be passed back
    /// into an IStorage method. <c>Path.Combine</c> uses the OS separator
    /// which is <c>'\\'</c> on Windows — violating Rule 2 of the path contract.
    /// </summary>
    string CombinePath(string parent, params string[] child) => Driver.CombinePath(parent, child);

    /// <summary>
    /// Returns the last segment of a storage-relative path — the storage-aware
    /// equivalent of <c>System.IO.Path.GetFileName</c>. Splits on <c>'/'</c>
    /// (Rule 2 canonical separator). Never touches the local filesystem.
    /// </summary>
    string GetName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    /// <summary>
    /// Returns the parent directory segment of a storage-relative path — the
    /// storage-aware equivalent of <c>System.IO.Path.GetDirectoryName</c>.
    /// Splits on <c>'/'</c> (Rule 2). Returns null when the path has no parent
    /// (i.e. it is already the scope root).
    /// </summary>
    string? GetParent(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;
        string trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        if (idx < 0)
            return null;
        string parent = trimmed[..idx];
        return string.IsNullOrEmpty(parent) ? null : parent;
    }

    /// <summary>
    /// Returns the last segment of a storage-relative path without its
    /// extension — the storage-aware equivalent of
    /// <c>System.IO.Path.GetFileNameWithoutExtension</c>.
    /// </summary>
    string GetNameWithoutExtension(string path)
    {
        string name = GetName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// Resolves a storage-relative path to the absolute local filesystem
    /// path that would be written for that key. Only meaningful for
    /// <see cref="Drivers.Local.LocalStorage"/>; all other backends throw
    /// <see cref="NotSupportedException"/>. Used by the encoder
    /// orchestrator's same-volume rename fast-path.
    /// </summary>
    /// <remarks>
    /// The returned path is OS-absolute — it is intentionally NOT scope-relative.
    /// Do not pass the result back into IStorage methods (Rule 1 violation).
    /// This method escapes the abstraction by design; its only approved use is
    /// the encoder's same-volume rename fast-path.
    /// </remarks>
    string GetFullPath(string path) =>
        throw new NotSupportedException(
            $"{GetType().Name} does not support GetFullPath. "
                + "This method is only valid for LocalStorage."
        );
}
