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

namespace NoMercy.Storage.Validation;

/// <summary>
/// Validates every path that flows through <see cref="IStorage"/>
/// before it reaches a driver. Two layers:
///   1. Structural — empty / null-byte / Windows device paths are
///      rejected unconditionally.
///   2. Allowlist — when configured, the canonical path (after symlink
///      resolution) must sit under one of the allowed roots.
/// Empty allowlist = structural checks only — used during the encoder
/// Phase 0 migration before consumers have populated their roots.
/// </summary>
public sealed partial class StoragePathGuard
{
    private readonly IStorageDriver _driver;
    private readonly string[] _normalizedRoots;
    private readonly StringComparison _comparison;

    public bool Enforced => _normalizedRoots.Length > 0;

    public IReadOnlyList<string> AllowedRoots => _normalizedRoots;

    public StoragePathGuard(IEnumerable<string> allowedRoots, IStorageDriver driver)
    {
        _driver = driver ?? throw new ArgumentNullException(nameof(driver));
        _comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        _normalizedRoots =
        [
            .. (allowedRoots ?? [])
                .Where(static r => !string.IsNullOrWhiteSpace(r))
                .Select(NormalizeRoot)
                .Distinct(StringComparerFromComparison.For(_comparison)),
        ];
    }

    /// <summary>
    /// Structural-only validation: rejects null bytes, ".." traversal, and
    /// Windows device paths. Does NOT canonicalize, does NOT enforce the
    /// under-root rule, and does NOT reject OS-absolute paths — that check
    /// is the caller's responsibility when operating in a remote-driver
    /// context (see <see cref="RejectAbsolutePath"/>).
    /// Empty/null is accepted — drivers treat "" as the scope root (Rule 3).
    /// </summary>
    public static void StructuralValidate(string? requestedPath)
    {
        if (requestedPath is null || requestedPath.Length == 0)
            return;

        if (requestedPath.Contains('\0'))
            throw new StoragePathNotAllowedException(requestedPath, "null byte in path");

        if (
            requestedPath == ".."
            || requestedPath.StartsWith("../", StringComparison.Ordinal)
            || requestedPath.StartsWith("..\\", StringComparison.Ordinal)
            || requestedPath.EndsWith("/..", StringComparison.Ordinal)
            || requestedPath.EndsWith("\\..", StringComparison.Ordinal)
            || requestedPath.Contains("/../", StringComparison.Ordinal)
            || requestedPath.Contains(@"\..\", StringComparison.Ordinal)
        )
            throw new StoragePathNotAllowedException(requestedPath, ".. traversal is not allowed");

        if (
            OperatingSystem.IsWindows()
            && (
                requestedPath.StartsWith(@"\\?\", StringComparison.Ordinal)
                || requestedPath.StartsWith(@"\\.\", StringComparison.Ordinal)
            )
        )
            throw new StoragePathNotAllowedException(requestedPath, "device paths are not allowed");
    }

    /// <summary>
    /// Rejects any path that is rooted in OS or backend-absolute terms.
    /// These forms are never valid scope-relative keys for remote drivers:
    ///   - leading '/' or '\'  (Unix root, UNC share, or Windows backslash root)
    ///   - Windows drive prefix  X:
    /// Must be called by remote-driver entry points (e.g. RemoteStorage.V())
    /// in addition to <see cref="StructuralValidate"/>. Not called from
    /// <see cref="Validate"/> because LocalStorage passes OS-absolute paths
    /// through the structural check before the under-root allowlist check.
    /// </summary>
    public static void RejectAbsolutePath(string path)
    {
        if (path.Length == 0)
            return;

        char first = path[0];
        if (first == '/' || first == '\\')
            throw new StoragePathNotAllowedException(
                path,
                "absolute paths are not allowed as scope-relative keys"
            );

        if (path is [_, ':', ..] && char.IsLetter(path[0]))
            throw new StoragePathNotAllowedException(
                path,
                "absolute paths are not allowed as scope-relative keys"
            );
    }

    /// <summary>
    /// Cross-platform rooted-path check. True when <paramref name="path"/> is
    /// absolute under the CURRENT OS's own rules (<see cref="Path.IsPathRooted"/>)
    /// OR under Windows' rules (drive letter / UNC) regardless of which OS this
    /// process happens to run on.
    ///
    /// <see cref="Path.IsPathRooted"/> is native-OS-only: on Linux it returns
    /// false for <c>C:\Windows\System32</c> and <c>\\server\share\file</c>
    /// because backslash isn't a separator there, so a caller relying on it
    /// alone would treat those as scope-relative and let them bypass the
    /// allowlist entirely. A path that reaches this guard may have been
    /// authored on, or targets, a different OS than the one enforcing it
    /// (disk-scan results replayed on Linux CI, a UNC path pasted into a
    /// Linux-hosted config) so both notations must always be recognized,
    /// on every platform, as absolute.
    /// </summary>
    public static bool IsRootedAnyStyle(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        return Path.IsPathRooted(path)
            || WindowsDriveLetterRoot().IsMatch(path)
            || WindowsUncRoot().IsMatch(path);
    }

    [GeneratedRegex(@"^[A-Za-z]:[\\/]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsDriveLetterRoot();

    [GeneratedRegex(@"^\\\\", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WindowsUncRoot();

    /// <summary>
    /// Validates <paramref name="requestedPath"/> and returns its
    /// canonical absolute form. Throws
    /// <see cref="StoragePathNotAllowedException"/> on rejection.
    ///
    /// Path Contract Rule 3 (empty = root) is honoured at the IStorage level
    /// (LocalStorage.ValidateScoped resolves empty → root before calling here)
    /// — Validate itself still rejects empty so callers can't accidentally
    /// bypass scope resolution.
    /// </summary>
    public string Validate(string requestedPath)
    {
        if (string.IsNullOrWhiteSpace(requestedPath))
            throw new StoragePathNotAllowedException(requestedPath ?? "<null>", "path is empty");

        StructuralValidate(requestedPath);

        string canonical;
        try
        {
            canonical = _driver.GetFullPath(requestedPath);
        }
        catch (Exception ex)
        {
            throw new StoragePathNotAllowedException(
                requestedPath,
                $"cannot canonicalize: {ex.Message}"
            );
        }

        if (!Enforced)
            return canonical;

        string resolved = _driver.ResolveLinkTarget(canonical) ?? canonical;

        foreach (string root in _normalizedRoots)
            if (IsUnderRoot(resolved, root, _comparison))
                return canonical;

        throw new StoragePathNotAllowedException(
            requestedPath,
            $"path is not under any allowed root: [{string.Join(", ", _normalizedRoots)}]"
        );
    }

    // Allowlist roots are on-disk OS paths; canonicalizing them here is the
    // local-enforcement boundary, not a storage-contract path (NMS002).
#pragma warning disable NMS002
    private static string NormalizeRoot(string root) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
#pragma warning restore NMS002

    private static bool IsUnderRoot(string fullPath, string root, StringComparison cmp)
    {
        if (string.Equals(fullPath, root, cmp))
            return true;
        // A drive/volume root (e.g. "G:\", "/") already ends in a separator;
        // appending another would produce "G:\\" which no real child path starts
        // with, wrongly rejecting everything under it. Only add the separator
        // when the root does not already carry one.
        bool rootEndsWithSeparator =
            root.Length > 0
            && (
                root[^1] == Path.DirectorySeparatorChar
                || root[^1] == Path.AltDirectorySeparatorChar
            );
        string withSep = rootEndsWithSeparator ? root : root + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(withSep, cmp);
    }
}

internal static class StringComparerFromComparison
{
    public static StringComparer For(StringComparison c) =>
        c switch
        {
            StringComparison.Ordinal => StringComparer.Ordinal,
            StringComparison.OrdinalIgnoreCase => StringComparer.OrdinalIgnoreCase,
            StringComparison.CurrentCulture => StringComparer.CurrentCulture,
            StringComparison.CurrentCultureIgnoreCase => StringComparer.CurrentCultureIgnoreCase,
            StringComparison.InvariantCulture => StringComparer.InvariantCulture,
            StringComparison.InvariantCultureIgnoreCase =>
                StringComparer.InvariantCultureIgnoreCase,
            _ => StringComparer.Ordinal,
        };
}
