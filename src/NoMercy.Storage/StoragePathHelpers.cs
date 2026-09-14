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
/// Pure-string path helpers for storage-relative paths. Equivalent to
/// <c>System.IO.Path.GetFileName</c> / <c>GetDirectoryName</c> / <c>GetFileNameWithoutExtension</c>
/// but always splits on <c>'/'</c> (Rule 2 of the IStorage path contract),
/// never on <c>'\\'</c>. Use when no <see cref="IStorage"/> instance is
/// available (e.g. stateless generators called from both IStorage consumers
/// and tests).
/// </summary>
public static class StoragePathHelpers
{
    /// <summary>
    /// Returns the last forward-slash-delimited segment of
    /// <paramref name="path"/> — the storage equivalent of
    /// <c>System.IO.Path.GetFileName</c>.
    /// </summary>
    public static string GetName(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string trimmed = path.TrimEnd('/');
        int idx = trimmed.LastIndexOf('/');
        return idx < 0 ? trimmed : trimmed[(idx + 1)..];
    }

    /// <summary>
    /// Returns the parent directory segment of <paramref name="path"/> —
    /// the storage equivalent of <c>System.IO.Path.GetDirectoryName</c>.
    /// Returns null when the path has no parent (already the scope root).
    /// </summary>
    public static string? GetParent(string path)
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
    /// Returns the last segment of <paramref name="path"/> without its
    /// file extension — the storage equivalent of
    /// <c>System.IO.Path.GetFileNameWithoutExtension</c>.
    /// </summary>
    public static string GetNameWithoutExtension(string path)
    {
        string name = GetName(path);
        int dot = name.LastIndexOf('.');
        return dot < 0 ? name : name[..dot];
    }

    /// <summary>
    /// Joins <paramref name="parent"/> and <paramref name="child"/> with a
    /// single <c>'/'</c>, trimming redundant separators. Storage equivalent
    /// of <c>System.IO.Path.Combine</c>. Use <see cref="IStorage.CombinePath"/>
    /// instead when an <see cref="IStorage"/> instance is in scope.
    /// </summary>
    public static string Combine(string parent, string child)
    {
        if (string.IsNullOrEmpty(child))
            return parent;
        if (string.IsNullOrEmpty(parent))
            return child;
        string trimmedParent = parent.TrimEnd('/', '\\');
        string trimmedChild = child.TrimStart('/', '\\');
        return $"{trimmedParent}/{trimmedChild}";
    }

    /// <summary>
    /// Rebases a driver-absolute scan path (e.g.
    /// <c>"/mnt/vault/Media/Marvels/TV.Shows/What.If.(2021)/file.m3u8"</c>)
    /// onto its scope-relative folder root (<c>"Marvels/TV.Shows"</c>), yielding
    /// a facade-valid, backend-neutral key
    /// (<c>"Marvels/TV.Shows/What.If.(2021)/file.m3u8"</c>). MediaScan resolves
    /// every path through the driver, so it hands back absolute paths that the
    /// <see cref="IStorage"/> facade rejects on remote backends; rebasing gives
    /// callers a path the facade accepts and a portable value to persist.
    /// <para>
    /// The folder root is matched as a whole run of path segments — anchored on
    /// <c>'/'</c> (or the start/end of the string) on both sides — never a bare
    /// substring. A substring match let a root like <c>"TV.Shows"</c> match
    /// inside an unrelated, longer sibling segment (e.g. a mount or share named
    /// <c>"TV.Shows.Archive"</c>), which cut the rebase at the wrong point and
    /// produced an inconsistent <c>HostFolder</c> for the same file between
    /// scans. When the root is absent, the input is assumed already relative
    /// and returned with a trimmed leading slash — this branch is also what
    /// makes a second rebase of an already-rebased path a no-op: the root is
    /// still found, still anchored at the same segment boundary, so the result
    /// is unchanged.
    /// </para>
    /// </summary>
    public static string RebaseToFolderRoot(string absolutePath, string folderPath)
    {
        string normalizedItem = absolutePath.Replace('\\', '/');
        string normalizedRoot = folderPath.Replace('\\', '/').Trim('/');

        if (normalizedRoot.Length == 0)
            return normalizedItem.TrimStart('/');

        int rootIndex = FindAnchoredSegment(normalizedItem, normalizedRoot);
        return rootIndex < 0
            ? normalizedItem.TrimStart('/')
            : normalizedItem[rootIndex..].TrimStart('/');
    }

    /// <summary>
    /// Finds <paramref name="segment"/> in <paramref name="path"/> as a run of
    /// whole path segments — the match must start at the beginning of the string
    /// or right after a <c>'/'</c>, and end at the end of the string or right
    /// before a <c>'/'</c>. Plain <see cref="string.IndexOf(string)"/> would also
    /// accept a match landing mid-segment (<c>"TV.Shows"</c> inside
    /// <c>"TV.Shows.Archive"</c>), which is never the intended folder root.
    /// </summary>
    private static int FindAnchoredSegment(string path, string segment)
    {
        int searchFrom = 0;
        while (searchFrom <= path.Length - segment.Length)
        {
            int idx = path.IndexOf(segment, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (idx < 0)
                return -1;

            bool startsAtBoundary = idx == 0 || path[idx - 1] == '/';
            int endPos = idx + segment.Length;
            bool endsAtBoundary = endPos == path.Length || path[endPos] == '/';

            if (startsAtBoundary && endsAtBoundary)
                return idx;

            searchFrom = idx + 1;
        }

        return -1;
    }

    /// <summary>
    /// Splits a scanned file path into the <c>Folder</c> / <c>Filename</c> pair
    /// that a media row persists, relative to <paramref name="folderRoot"/>:
    /// <c>("/U2/The Joshua Tree", "/01. Where.flac")</c>. Both parts carry a
    /// leading <c>'/'</c> because the API composes the playback URL as
    /// <c>/{FolderId}{Folder}{Filename}</c>.
    /// Returns false — leaving both parts empty — when the pair could not
    /// address a file: an empty path or root, a path naming a directory, or a
    /// file that does not live under the root. Callers must not persist a row
    /// on false; the composed URL would be unresolvable for every client.
    /// Unlike <see cref="RebaseToFolderRoot"/>, which keeps the root segment for
    /// facade access, the returned folder is root-exclusive.
    /// </summary>
    public static bool TryGetLibraryRelativeParts(
        string absoluteFilePath,
        string folderRoot,
        out string folder,
        out string filename
    )
    {
        folder = string.Empty;
        filename = string.Empty;

        if (string.IsNullOrWhiteSpace(absoluteFilePath) || string.IsNullOrWhiteSpace(folderRoot))
            return false;

        string normalizedPath = absoluteFilePath.Replace('\\', '/');
        if (normalizedPath.EndsWith('/'))
            return false;

        string name = GetName(normalizedPath);
        if (string.IsNullOrEmpty(name))
            return false;

        string directory = normalizedPath[..^name.Length];
        if (!TryGetLibraryRelativeFolder(directory, folderRoot, out folder))
            return false;

        filename = "/" + name;
        return true;
    }

    /// <summary>
    /// The directory-shaped counterpart of
    /// <see cref="TryGetLibraryRelativeParts"/>: turns a scanned directory into
    /// the root-exclusive <c>Folder</c> value a media row persists
    /// (<c>"/U2/The Joshua Tree"</c>, or empty for the root itself). Returns
    /// false — leaving <paramref name="folder"/> empty — when the directory does
    /// not live under <paramref name="folderRoot"/>, so a caller never persists
    /// an unstripped host path such as <c>"M:/Download/complete/…"</c>.
    /// </summary>
    public static bool TryGetLibraryRelativeFolder(
        string directory,
        string folderRoot,
        out string folder
    )
    {
        folder = string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
            return false;

        string normalizedDirectory = directory.Replace('\\', '/');
        string normalizedRoot = (folderRoot ?? string.Empty).Replace('\\', '/').Trim('/');

        // An empty root means the folder itself IS the driver's scope root
        // (Folders.Path can be deliberately empty), so everything under it is
        // already library-relative — not "no root to resolve against".
        if (normalizedRoot.Length == 0)
        {
            folder = "/" + normalizedDirectory.Trim('/');
            return true;
        }

        int rootIndex = normalizedDirectory.IndexOf(
            normalizedRoot,
            StringComparison.OrdinalIgnoreCase
        );
        if (rootIndex < 0)
            return false;

        string withinRoot = normalizedDirectory[(rootIndex + normalizedRoot.Length)..].Trim('/');
        folder = withinRoot.Length == 0 ? string.Empty : "/" + withinRoot;
        return true;
    }
}
