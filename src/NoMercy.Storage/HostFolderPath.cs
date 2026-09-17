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
/// Three shapes are recognised. Two carry a marker and are what
/// <see cref="ContainsSecondRoot"/> answers to, so the importer can refuse them
/// on sight: a drive-letter root (<c>"…/album\Q:\…"</c>) and a share root
/// (<c>"…/album\\\nas\…"</c>). The third has no marker at all — a rooted Linux
/// path repeated (<c>"/mnt/music/album/mnt/music/album"</c>) is
/// indistinguishable from a real folder by shape alone — so only
/// <see cref="RepairDoubled"/> knows it, and only when the two halves are
/// character-for-character the same folder.
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
    /// the half before the doubling, when the half after it is the same folder,
    /// separator style aside. Null for anything else — a value that is not
    /// doubled, or two halves that name different folders, which is a value
    /// only a human can judge. The returned half keeps its original spelling,
    /// so a UNC root stays a UNC root.
    /// <para>
    /// Tries the second root first, then — for the marker-free Linux shape —
    /// every separator as a split point. The second pass answers only when the
    /// halves are provably the same folder, so it never widens what
    /// <see cref="ContainsSecondRoot"/> refuses; it only finds a repair the
    /// marker rules cannot see. It is still a shape, not a proof: the caller
    /// verifies the repaired path against the disk before it stores it.
    /// </para>
    /// </summary>
    public static string? RepairDoubled(string? hostFolder)
    {
        if (string.IsNullOrEmpty(hostFolder))
            return null;

        if (
            TrySplitAtSecondRoot(hostFolder, out string first, out string second)
            && CanonicalEquals(first, second)
        )
            return first;

        return RepairRepeatedHalf(hostFolder);
    }

    /// <summary>
    /// The marker-free case: a rooted path followed by the same rooted path,
    /// which on Linux carries nothing that says "a second path starts here".
    /// Walks every separator and answers at the first split whose halves are
    /// the same folder.
    /// </summary>
    private static string? RepairRepeatedHalf(string value)
    {
        for (int index = 1; index < value.Length - 1; index++)
        {
            if (!IsSeparator(value[index]))
                continue;

            string left = value[..index];
            string right = value[index..];

            if (!CanonicalEquals(left, right))
                continue;

            // A value that is nothing but separators has two equal halves and
            // no folder in it; repairing it to the empty string would take the
            // row from wrong to unaddressable.
            string repaired = left.TrimEnd('/', '\\');
            if (repaired.Length > 0)
                return repaired;
        }

        return null;
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
    /// <para>
    /// A drive letter has to be a root on both sides — a separator before it and
    /// a separator after the colon — or a folder whose name merely starts with a
    /// letter and a colon (<c>"/music/D:Ream/album"</c>) would cost the track
    /// its import.
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

            if (
                next == ':'
                && char.IsLetter(current)
                && IsSeparator(value[index - 1])
                && index + 2 < value.Length
                && IsSeparator(value[index + 2])
            )
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
    private static bool CanonicalEquals(string left, string right) =>
        Canonical(left).Equals(Canonical(right), StringComparison.OrdinalIgnoreCase);

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
