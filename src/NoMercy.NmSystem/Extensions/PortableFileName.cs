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

namespace NoMercy.NmSystem.Extensions;

/// <summary>
/// The one guard every generated file or folder name goes through. A name written by one
/// host must be readable from Windows, Linux and macOS on a shared drive, so the rules are
/// the strictest of the three and do not depend on the host running the server.
/// </summary>
public static class PortableFileName
{
    public const int MaxSegmentBytes = 255;

    private const char Replacement = '_';
    private const int MaxExtensionLength = 16;

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON",
        "PRN",
        "AUX",
        "NUL",
        "COM1",
        "COM2",
        "COM3",
        "COM4",
        "COM5",
        "COM6",
        "COM7",
        "COM8",
        "COM9",
        "LPT1",
        "LPT2",
        "LPT3",
        "LPT4",
        "LPT5",
        "LPT6",
        "LPT7",
        "LPT8",
        "LPT9",
    };

    /// <summary>One file or folder name, never a path: a slash in it is replaced.</summary>
    public static string Segment(string name)
    {
        StringBuilder builder = new(name.Length);
        foreach (char character in name)
            builder.Append(IsReserved(character) ? Replacement : character);

        string cleaned = TrimTrailingDotsAndSpaces(builder.ToString());
        if (cleaned.Length == 0)
            return Replacement.ToString();

        cleaned = GuardDeviceName(cleaned);
        return LimitLength(cleaned);
    }

    /// <summary>A relative or rooted path joined with slashes: each segment goes through <see cref="Segment"/>.</summary>
    public static string Path(string path)
    {
        string[] segments = path.Split('/');
        for (int index = 0; index < segments.Length; index++)
            if (segments[index].Length > 0 && segments[index] != ".")
                segments[index] = Segment(segments[index]);

        return string.Join('/', segments);
    }

    private static bool IsReserved(char character) =>
        char.IsControl(character)
        || character is '?' or ':' or '*' or '"' or '<' or '>' or '|' or '\\' or '/';

    private static string TrimTrailingDotsAndSpaces(string name)
    {
        string trimmed = name.TrimEnd('.', ' ');
        return trimmed.Length == name.Length ? name : trimmed + Replacement;
    }

    // Windows reads CON.flac as the console device too. Only a name that ends in one short
    // extension is checked: a title like "Con.Air.(1997)" already exists on Linux libraries.
    private static string GuardDeviceName(string name)
    {
        int dot = name.IndexOf('.');
        string extension = dot < 0 ? string.Empty : name[(dot + 1)..];
        if (extension.Length > 5 || !extension.All(char.IsAsciiLetterOrDigit))
            return name;

        string stem = dot < 0 ? name : name[..dot];
        return ReservedNames.Contains(stem.TrimEnd(' '))
            ? stem + Replacement + (dot < 0 ? string.Empty : name[dot..])
            : name;
    }

    private static string LimitLength(string name)
    {
        if (Encoding.UTF8.GetByteCount(name) <= MaxSegmentBytes)
            return name;

        int dot = name.LastIndexOf('.');
        string extension =
            dot > 0 && name.Length - dot <= MaxExtensionLength ? name[dot..] : string.Empty;
        int budget = MaxSegmentBytes - Encoding.UTF8.GetByteCount(extension);

        StringBuilder stem = new();
        int used = 0;
        foreach (Rune rune in name[..(name.Length - extension.Length)].EnumerateRunes())
        {
            used += rune.Utf8SequenceLength;
            if (used > budget)
                break;
            stem.Append(rune.ToString());
        }

        return TrimTrailingDotsAndSpaces(stem.ToString()) + extension;
    }
}
