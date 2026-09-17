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

using System.Text;

namespace NoMercy.Storage;

/// <summary>
/// Recognises — and, where it is safe, undoes — a <c>HostFolder</c> that holds
/// two rooted paths instead of one.
/// <para>
/// An older importer run wrote rows whose <c>HostFolder</c> is the album folder
/// twice: the folder with forward slashes, a separator, then the same folder
/// with backslashes. <c>Filename</c> is still the bare file name, so every
/// consumer that combines the two (playback, subtitles, transcodes, the audio
/// analysis job, the plugin audio tools) builds a path that exists nowhere.
/// A path helper cannot repair that after the fact, so the importer refuses to
/// write such a value and a startup sweep repairs the rows already stored.
/// </para>
/// <para>
/// Pure string work: no disk access, no separator assumptions beyond "a
/// separator is <c>'/'</c> or <c>'\'</c>", so it holds for local paths, UNC
/// shares and remote driver keys alike.
/// </para>
/// </summary>
public static class HostFolderPath
{
    /// <summary>
    /// True when <paramref name="hostFolder"/> contains a second rooted path:
    /// a drive-letter root (<c>"X:"</c>) preceded by a separator, or a share
    /// root (<c>"//"</c> or <c>"\\"</c>) anywhere after the first character.
    /// A leading root is the path's own and is never counted.
    /// </summary>
    public static bool ContainsSecondRoot(string? hostFolder) =>
        TrySplitAtSecondRoot(hostFolder, out _, out _);

    /// <summary>
    /// Splits <paramref name="hostFolder"/> at the second rooted path it
    /// contains: <paramref name="first"/> is everything before it (trailing
    /// separators trimmed), <paramref name="second"/> is the second root and
    /// everything after it. False — with both parts empty — when the value
    /// holds no second root, or when nothing precedes it.
    /// </summary>
    public static bool TrySplitAtSecondRoot(string? hostFolder, out string first, out string second)
    {
        first = string.Empty;
        second = string.Empty;

        if (string.IsNullOrEmpty(hostFolder))
            return false;

        int index = FindSecondRoot(hostFolder);
        if (index < 0)
            return false;

        string head = hostFolder[..index].TrimEnd('/', '\\');
        if (head.Length == 0)
            return false;

        first = head;
        second = hostFolder[index..];
        return true;
    }

    /// <summary>
    /// Returns the single folder a doubled <c>HostFolder</c> was built from:
    /// the part before the second root, when the part from the second root is
    /// the same folder written with the other separator. Null for anything
    /// else — no second root at all, or two halves that name different folders,
    /// which is a value only a human can judge. The returned half keeps its
    /// original spelling, so a UNC root stays a UNC root.
    /// </summary>
    public static string? RepairDoubled(string? hostFolder)
    {
        if (!TrySplitAtSecondRoot(hostFolder, out string first, out string second))
            return null;

        return Canonical(first).Equals(Canonical(second), StringComparison.OrdinalIgnoreCase)
            ? first
            : null;
    }

    /// <summary>
    /// Index of the first character of a second rooted path, or -1. The scan
    /// starts at index 1 so a path's own leading root — <c>"C:/…"</c>,
    /// <c>"//nas/…"</c> — is never mistaken for a second one.
    /// <para>
    /// A run of two separators counts as a share root wherever it appears after
    /// the first character. That also rejects a value carrying a doubled
    /// separator mid-path, which no scanner should produce and which no
    /// consumer can be trusted to resolve: refusing it loudly is the point.
    /// </para>
    /// </summary>
    private static int FindSecondRoot(string value)
    {
        for (int index = 1; index + 1 < value.Length; index++)
        {
            char current = value[index];
            char next = value[index + 1];

            if (IsSeparator(current) && IsSeparator(next))
                return index;

            if (next == ':' && char.IsLetter(current) && IsSeparator(value[index - 1]))
                return index;
        }

        return -1;
    }

    /// <summary>
    /// One comparable spelling of a folder: every separator becomes
    /// <c>'/'</c>, repeated separators collapse to one, trailing separators go.
    /// Only ever used to compare the two halves of one value against each
    /// other, never to build a path a driver is handed.
    /// </summary>
    private static string Canonical(string value)
    {
        StringBuilder builder = new(value.Length);
        bool previousWasSeparator = false;

        foreach (char character in value)
        {
            char normalized = IsSeparator(character) ? '/' : character;

            if (normalized == '/' && previousWasSeparator)
                continue;

            builder.Append(normalized);
            previousWasSeparator = normalized == '/';
        }

        return builder.ToString().TrimEnd('/');
    }

    private static bool IsSeparator(char character) => character is '/' or '\\';
}
