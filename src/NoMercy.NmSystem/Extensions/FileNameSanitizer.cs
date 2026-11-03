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

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using NoMercy.Storage;

namespace NoMercy.NmSystem.Extensions;

public static partial class FileNameSanitizer
{
    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRegex();

    [GeneratedRegex(@"[^\u0000-\u007FÀ-ſĀ-ɏ]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAsciiRegex();

    [GeneratedRegex(@"\.+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepeatedDotsRegex();

    [GeneratedRegex(@"[/\\|:*?\""<>{}]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathSeparatorsRegex();

    [GeneratedRegex(@"[/\\:*?""<>|\p{Cc}]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvalidFileNameCharsRegex();

    private static string _cleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        // The same set on every host: Path.GetInvalidFileNameChars is shorter on Linux.
        name = InvalidFileNameCharsRegex().Replace(name, ".");

        // Replace whitespace with dots
        name = WhitespaceRegex().Replace(name, ".");

        // Replace special characters and symbols in a single pass
        StringBuilder sb = new(name.Length + 16);
        foreach (char c in name)
        {
            switch (c)
            {
                case '\u2010': // Hyphen
                case '\u2013': // En dash
                case '\u2014': // Em dash
                case '\u2212': // Minus sign
                    sb.Append('-');
                    break;
                case '\u00B0': // Degree sign
                    sb.Append(".Degrees");
                    break;
                case '&':
                    sb.Append("and");
                    break;
                case '!':
                case '?':
                case '~':
                case '`':
                    sb.Append('.');
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        name = sb.ToString();

        // Replace any remaining non-ASCII characters with dots
        name = NonAsciiRegex().Replace(name, ".");

        // Collapse multiple dots
        name = RepeatedDotsRegex().Replace(name, ".");

        // Remove leading/trailing dots
        name = name.Trim('.');

        return name.Length == 0 ? name : PortableFileName.Segment(name);
    }

    public static string SanitizeFileName(this string filePath)
    {
        string directory = Path.GetDirectoryName(filePath).OrEmpty();
        string fileName = Path.GetFileName(filePath);

        // Replace problematic Unicode characters with ASCII equivalents
        fileName = fileName
            .Replace('\u2019', '\'') // Right single quote
            .Replace('\u2018', '\'') // Left single quote
            .Replace('\u201C', '"') // Left double quote
            .Replace('\u201D', '"') // Right double quote
            .Replace('\u2013', '-') // En dash
            .Replace('\u2014', '-'); // Em dash

        // Normalize to decomposed form (separates combined characters)
        fileName = fileName.Normalize(NormalizationForm.FormKD);

        return Path.Combine(directory, fileName);
    }

    public static string DirectorySafeName(this string? self)
    {
        if (string.IsNullOrEmpty(self))
            return string.Empty;
        string name = PathSeparatorsRegex().Replace(self, " ");
        return PortableFileName.Segment(name.Trim().SanitizeFileName());
    }

    public static string MusicBrainzSafeName(this string? self)
    {
        if (string.IsNullOrEmpty(self))
            return string.Empty;
        string name = PathSeparatorsRegex().Replace(self, "_");
        return PortableFileName.Segment(name.Trim().SanitizeFileName());
    }

    public static string CleanFileName(this string? self)
    {
        return _cleanFileName(self);
    }

    /// <summary>
    /// Default cap for a single title component inside a generated path segment.
    /// The show title is embedded in the show folder, the episode/season folder
    /// AND the filename, and the V3 encoder nests an HLS bundle (~90 chars) under
    /// that directory — so an unbounded anime-length title blows past Windows'
    /// 260-char path limit and playback fails. 50 leaves the overwhelming majority
    /// of real titles untouched (so existing on-disk names never move) while
    /// bounding the pathological ones.
    /// </summary>
    public const int MaxTitleComponentLength = 50;

    private static readonly char[] TitleTokenBoundaries = [' ', '.', '-', '_'];

    /// <summary>
    /// Bounds a single title component to <paramref name="maxLength"/> characters
    /// without mingling it with the season/episode markers or the episode title it
    /// gets concatenated with. A value already within the limit is returned
    /// unchanged, so short titles keep their exact existing paths. An over-long
    /// value is cut on a token boundary, which keeps the name readable — it is
    /// still the start of the real title, just fewer words of it.
    ///
    /// Nothing is appended to disambiguate. The name this lands in already
    /// carries what separates two media items: an episode has its SxxEyy marker,
    /// a movie its release year. A digest here bought nothing they don't already
    /// provide, cost nine of the fifty characters, and turned a title into
    /// something unreadable.
    ///
    /// Deterministic — the same title always produces the same shortened form, so
    /// a later rescan reconstructs the identical path.
    /// </summary>
    public static string Shorten(this string? self, int maxLength = MaxTitleComponentLength)
    {
        if (string.IsNullOrEmpty(self) || self.Length <= maxLength)
            return self ?? string.Empty;

        string head = self[..maxLength];
        int boundary = head.LastIndexOfAny(TitleTokenBoundaries);
        if (boundary >= maxLength / 2)
            head = head[..boundary];

        return head.Trim('.', ' ', '-', '_');
    }

    /// <summary>
    /// The key two folder or file names are compared by. "&amp;" reads as "and",
    /// the name is folded to its compatibility-composed form (NFKC), case is
    /// folded, and only the letters and digits of every script are kept, each
    /// with the combining marks written on it.
    /// <list type="bullet">
    /// <item>NFKC makes the same name written differently compare equal: a
    /// decomposed "e" plus acute (as macOS hands out names) is the composed "é",
    /// full-width "ＮＡＲＵＴＯ" is "naruto", half-width "ｶﾞﾝﾀﾞﾑ" is "ガンダム".</item>
    /// <item>Letters and digits are read per code point, so an ideograph outside
    /// the 16-bit range ("𠮷") is kept instead of dropped as two surrogates.</item>
    /// <item>A combining mark on a kept letter stays: a Devanagari or Thai vowel
    /// sign is what tells two words apart. A mark on anything else (an emoji's
    /// variation selector) goes with what it sits on.</item>
    /// <item>Accents are deliberately NOT folded: "Amélie" and "Amelie" differ.
    /// Folding them means stripping combining marks after decomposition, which
    /// would also strip the dakuten that tells が from か.</item>
    /// </list>
    /// A name with no letters or digits at all ("---") yields an empty key, which
    /// says nothing about the name; callers must not treat two empty keys as a match.
    /// </summary>
    public static string NormalizeForComparison(this string name)
    {
        string composed = name.Replace("&", "and").Normalize(NormalizationForm.FormKC);
        StringBuilder key = new(composed.Length);
        bool previousKept = false;
        foreach (Rune rune in composed.EnumerateRunes())
        {
            bool keep = Rune.IsLetterOrDigit(rune) || (previousKept && IsCombiningMark(rune));
            if (keep)
                key.Append(Rune.ToLowerInvariant(rune).ToString());
            previousKept = keep && !Rune.IsDigit(rune);
        }

        return key.ToString();
    }

    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune)
            is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark;

    public static string? FindMatchingDirectory(
        IStorageDriver driver,
        string rootPath,
        string expectedFolderName
    )
    {
        if (!driver.DirectoryExists(rootPath))
            return null;

        // A name with no letters or digits normalises to an empty key that every
        // other such name shares, so it matches only a folder named exactly like it.
        string normalizedExpected = expectedFolderName.NormalizeForComparison();
        bool exactOnly = normalizedExpected.Length == 0;

        foreach (
            string dir in driver.EnumerateFileSystemEntries(
                rootPath,
                "*",
                SearchOption.TopDirectoryOnly
            )
        )
        {
            if (!driver.DirectoryExists(dir))
                continue;
            string dirName = Path.GetFileName(dir).OrEmpty();
            bool matches = exactOnly
                ? string.Equals(dirName, expectedFolderName, StringComparison.Ordinal)
                : dirName.NormalizeForComparison() == normalizedExpected;
            if (matches)
                return dir;
        }

        return null;
    }
}
