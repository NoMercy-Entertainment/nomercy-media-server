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
using System.Text.RegularExpressions;
using NoMercy.Storage;

namespace NoMercy.NmSystem.Extensions;

public static class FileNameSanitizer
{
    private static string _cleanFileName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return "";

        // Replace invalid file system characters with dots
        string invalidChars = $"{string.Join("", Path.GetInvalidFileNameChars())}:?*<>|\"";
        string pattern = $"[{Regex.Escape(invalidChars)}]";
        name = Regex.Replace(name, pattern, ".");

        // Replace whitespace with dots
        name = Regex.Replace(name, @"\s+", ".");

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
        name = Regex.Replace(name, @"[^\u0000-\u007F\u00C0-\u017F\u0100-\u024F]+", ".");

        // Collapse multiple dots
        name = Regex.Replace(name, @"\.+", ".");

        // Remove leading/trailing dots
        name = name.Trim('.');

        return name;
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
        string name = Regex.Replace(self, @"[/\\|:*?\""<>{}]", " ");
        return name.Trim().SanitizeFileName();
    }

    public static string MusicBrainzSafeName(this string? self)
    {
        if (string.IsNullOrEmpty(self))
            return string.Empty;
        string name = Regex.Replace(self, @"[/\\|:*?\""<>{}]", "_");
        return name.Trim().SanitizeFileName();
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

    public static string NormalizeForComparison(this string name)
    {
        name = name.Replace("&", "and");
        // Keep every Unicode letter and digit, so a Japanese, Korean or Cyrillic
        // name does not collapse to an empty string that equals every other one.
        return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
    }

    public static string? FindMatchingDirectory(
        IStorageDriver driver,
        string rootPath,
        string expectedFolderName
    )
    {
        if (!driver.DirectoryExists(rootPath))
            return null;

        string normalizedExpected = expectedFolderName.NormalizeForComparison();
        if (normalizedExpected.Length == 0)
            return null;

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
            if (dirName.NormalizeForComparison() == normalizedExpected)
                return dir;
        }

        return null;
    }
}
