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

using System.Diagnostics.Contracts;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NoMercy.NmSystem.Extensions;

public static partial class StringExtensions
{
    public static string DirectorySeparator => Path.DirectorySeparatorChar.ToString();

    [Pure]
    public static string RemoveAccents(this string s)
    {
        Encoding destEncoding = Encoding.GetEncoding("ISO-8859-1");

        return destEncoding.GetString(
            Encoding.Convert(Encoding.UTF8, destEncoding, Encoding.UTF8.GetBytes(s))
        );
    }

    [Pure]
    public static string RemoveDiacritics(this string text)
    {
        string formD = text.Normalize(NormalizationForm.FormD);
        StringBuilder sb = new();

        foreach (char ch in formD)
        {
            UnicodeCategory uc = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (uc != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    public static string RemoveNonAlphaNumericCharacters(this string text)
    {
        return NonAlphaNumericRegex().Replace(text, "");
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\s.-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"\s+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex WhitespaceRunRegex();

    [GeneratedRegex(@"(1(8|9)|20)[0-9]{2}(?!p|i|(1(8|9)|20)[0-9]{2}|\W(1(8|9)|20)[0-9]{2})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchYearRegex();

    public static string? TryGetYear(this string str)
    {
        Match match = MatchYearRegex().Match(str);
        return match.Success ? match.Value : null;
    }

    [GeneratedRegex(@"\[.*?\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex RemoveBracketedString();

    /// <summary>Matches a [tmdb-1234] hint embedded in a filename.</summary>
    [GeneratedRegex(@"\[tmdb-([0-9]+)\]", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchTmdbHint();

    /// <summary>
    /// Extracts the TMDB ID embedded as <c>[tmdb-1234]</c> anywhere in the string,
    /// or returns <see langword="null"/> if no hint is present.
    /// </summary>
    public static int? TryGetTmdbHint(this string str)
    {
        Match m = MatchTmdbHint().Match(str);
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }

    /// <summary>
    /// ASCII digits only, here and in every other numbering pattern. <c>\d</c>
    /// matches any Unicode decimal digit and <c>int.Parse</c> reads none of
    /// them, so a real release — "First Season 「アタックオブ朹町２丁目…」" —
    /// matched the fullwidth ２ and threw <c>FormatException</c> out of the
    /// scan. One such file in a library stopped the whole import.
    /// </summary>
    [GeneratedRegex(@"[0-9]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchNumbers();

    // '+' joins the earlier one is included: a BD batch folder like
    // "...Ah! My Goddess!... S1+S2+Movie+OVAs [BD]..." names every season it
    // contains with '+' rather than a space/dash/underscore, so "S1" was never
    // bounded on its right and the whole "S1+S2+Movie+OVAs" tail leaked into
    // the show title handed to TitleFromFolder.
    [GeneratedRegex(@"[\.\s\-_+]S[0-9]{1,2}(?:E[0-9]+)?(?:[\.\s\-_+]|$)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchSeasonTag();

    [GeneratedRegex(@"^S([0-9]{1,2})E([0-9]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchEpisodePrefix();

    /// <summary>
    /// S##E## preceded by a separator. Brackets count as separators because fansub
    /// releases write the marker as "(S02E01)" or "[S01E05]"; without them such files fall
    /// through to the generic detector, which reads the release's absolute episode number as
    /// the episode and searches for a title that includes it.
    /// </summary>
    [GeneratedRegex(@"[\.\s\-_\(\[\{]S([0-9]{1,2})E([0-9]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchSeasonEpisode();

    [GeneratedRegex(@"\(.*?\)", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex RemoveParenthesizedString();

    [GeneratedRegex(@"[\-\.\s]+Episode\s*([0-9]+)", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchEpisodeWord();

    [GeneratedRegex(@"(?<![A-Za-z0-9])([0-9]{1,2})[xX×]([0-9]{1,3})(?![0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchCrossFormatEpisode();

    /// <summary>
    /// Scene "quality" vocabulary, split per category so each table is named and
    /// individually testable. Vocabulary follows the scene rules (scenerules.org);
    /// cf. the MIT-licensed pr0pz/scene-release-parser for the canonical tables.
    /// Every token is bounded by non-alphanumeric characters so a real title word
    /// that merely contains a token as a substring ("Limitless", "Web Therapy") is
    /// never matched, and internal separators are tolerated so dotted/hyphenated
    /// forms ("WEB-DL", "H.264", "DDP5.1") match the same as spaced forms.
    /// </summary>
    /// <summary>Resolution tags: 480p/720p/1080p/2160p (interlaced too), 4k/8k, uhd.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:\d{3,4}[pi]|[48]k|uhd)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchResolutionTag();

    /// <summary>Source / medium tags (web-dl, web-rip, bluray, hdtv, dvd*, remux, streaming services, ...).</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:web[\s.\-]?dl|web[\s.\-]?rip|web[\s.\-]?hd|blu[\s.\-]?ray|bd[\s.\-]?rip|b[rd][\s.\-]?rip|hd[\s.\-]?tv|pd[\s.\-]?tv|dvd[\s.\-]?rip|hd[\s.\-]?rip|hd[\s.\-]?cam|uhd[\s.\-]?bd|remux|hd[\s.\-]?dvd|hd[\s.\-]?tc|ed[\s.\-]?tv|dvd[\s.\-]?scr|dvdscr|dsr|amzn|dsnp|atvp|hmax|hulu|nflx|hd[\s.\-]?light|sdtv|dvb|vod[\s.\-]?rip|tv[\s.\-]?rip|sat[\s.\-]?rip|dth[\s.\-]?rip|web[\s.\-]?cap|hd[\s.\-]?ts|telesync|telecine|workprint|ld[\s.\-]?rip|dvdr|ppv|screener)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchSourceTag();

    /// <summary>Video codec tags (x264/5, h264/5, hevc, xvid, divx, avc, vc-1, wmv, mpeg(2), vp8/9).</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:x[\s.\-]?26[45]|h[\s.\-]?26[45]|hevc|xvid|divx|avc|vc[\s.\-]?1|wmv|mpeg2?|vp[89]|av1|vvc|h[\s.\-]?266|mpeg4)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchCodecTag();

    /// <summary>Audio tags (ddp5.1, eac3, dts(-hd/ma/es/x), truehd, atmos, aac, flac, ac3d, mp3).</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:ddp?[\s.\-]?\d(?:[\s.\-]?\d)?|e?ac[\s.\-]?3|dts(?:[\s.\-]?hd)?(?:[\s.\-]?ma)?|dts[\s.\-]?(?:es|x)|true[\s.\-]?hd|atmos|aac(?:[\s.\-]?\d(?:[\s.\-]?\d)?)?|flac|ac3d|eac3d|mp3|lpcm|dd\+|ddp\+)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchAudioTag();

    /// <summary>HDR / bit-depth tags (10bit, hdr10+, hdr, dovi, dolby vision, hlg).</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:\d{1,2}[\s.\-]?bit|hdr10\+?|hdr|do[\s.\-]?vi|dolby[\s.\-]?vision|hlg|sdr)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchHdrTag();

    /// <summary>Release flags / editions (repack, multi, imax).</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:repack|multi|imax|dir[\s.\-]?fix|nfo[\s.\-]?fix|read[\s.\-]?nfo|proof[\s.\-]?fix|re[\s.\-]?rip)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchFlagTag();

    /// <summary>The scene tag categories recognised by the per-category release regexes.</summary>
    public enum ReleaseTagCategory
    {
        Resolution,
        Source,
        Codec,
        Audio,
        Hdr,
        Flag,
    }

    /// <summary>
    /// Finds the earliest (left-most) scene tag of any category in <paramref name="text"/>,
    /// returning its start index plus the matched category/value, or -1 when none match.
    /// </summary>
    private static int EarliestReleaseTag(
        string text,
        out ReleaseTagCategory category,
        out string value
    )
    {
        int best = int.MaxValue;
        ReleaseTagCategory bestCat = default;
        string bestVal = string.Empty;

        void Take(Match m, ReleaseTagCategory cat)
        {
            if (m.Success && m.Index < best)
            {
                best = m.Index;
                bestCat = cat;
                bestVal = m.Value;
            }
        }

        Take(MatchResolutionTag().Match(text), ReleaseTagCategory.Resolution);
        Take(MatchSourceTag().Match(text), ReleaseTagCategory.Source);
        Take(MatchCodecTag().Match(text), ReleaseTagCategory.Codec);
        Take(MatchAudioTag().Match(text), ReleaseTagCategory.Audio);
        Take(MatchHdrTag().Match(text), ReleaseTagCategory.Hdr);
        Take(MatchFlagTag().Match(text), ReleaseTagCategory.Flag);

        category = bestCat;
        value = bestVal;
        return best == int.MaxValue ? -1 : best;
    }

    /// <summary>
    /// Attempts to find the first scene quality/source/codec/audio/HDR/flag tag in a
    /// release name, reporting the matched <paramref name="value"/> and its category.
    /// </summary>
    [Pure]
    public static bool TryGetReleaseTag(
        this string raw,
        out string value,
        out ReleaseTagCategory category
    )
    {
        category = default;
        value = string.Empty;
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        string normalized = WhitespaceRunRegex().Replace(raw.Replace('.', ' ').Replace('_', ' '), " ").Trim();
        return EarliestReleaseTag(normalized, out category, out value) >= 0;
    }

    /// <summary>
    /// Cleans a derived show/movie title by normalizing separators and removing
    /// everything from the first recognized scene tag onward (resolution, source,
    /// codec, audio, HDR and release flags). Year extraction is intentionally left
    /// to the caller. Returns the trimmed leading portion of the title.
    /// </summary>
    [Pure]
    public static string CleanReleaseTitle(this string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        string normalized = raw.Replace('.', ' ').Replace('_', ' ');
        normalized = WhitespaceRunRegex().Replace(normalized, " ").Trim();

        int cut = EarliestReleaseTag(normalized, out _, out _);
        string title = cut >= 0 ? normalized[..cut] : normalized;

        title = title.TrimEnd(' ', '-', '_', '.');
        return WhitespaceRunRegex().Replace(title, " ").Trim();
    }

    /// <summary>
    /// Series-aware title cleanup: applies <see cref="CleanReleaseTitle"/> and then
    /// drops a trailing release year (e.g. "Halo 2022" -> "Halo", "New Amsterdam
    /// 2018" -> "New Amsterdam"). A year at the very start is kept so shows that are
    /// literally named by a year ("1883", "1923", "1899") survive intact.
    /// </summary>
    [Pure]
    public static string CleanSeriesTitle(this string raw)
    {
        string title = raw.CleanReleaseTitle();

        Match year = MatchYearRegex().Match(title);
        if (year is { Success: true, Index: > 0 })
            title = title[..year.Index];

        // Cutting at the year's index splits a parenthesised one down the middle,
        // so "Dororo (2019) - 01" left the show named "Dororo (" — which matches
        // nothing at any provider. A title never legitimately ends on an opening
        // bracket.
        return title.TrimEnd('-', '.', '_', ' ', '(', '[', '{').Trim();
    }

    /// <summary>
    /// The last segment of <paramref name="path"/>, treating BOTH separators as
    /// separators.
    /// <para><see cref="Path.GetFileName(string)"/> cannot: on Linux a backslash
    /// is an ordinary filename character, so "C:\Shows\Bleach\Season 2" is one
    /// segment there and the season folder stops being recognised as one. The
    /// paths reaching this come from a Windows picker or a NAS share as often as
    /// from the local disk, so the separator is data rather than a property of
    /// the machine reading it.</para>
    /// </summary>
    [Pure]
    public static string LeafSegment(this string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        int lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        return lastSeparator < 0 ? trimmed : trimmed[(lastSeparator + 1)..];
    }

    /// <summary>Everything before <see cref="LeafSegment"/>, or empty at the root.</summary>
    [Pure]
    public static string ParentSegment(this string path)
    {
        string trimmed = path.TrimEnd('/', '\\');
        int lastSeparator = trimmed.LastIndexOfAny(['/', '\\']);
        return lastSeparator <= 0 ? string.Empty : trimmed[..lastSeparator];
    }

    /// <summary>Matches a "Season N" / "Series N" (and common localized) folder name.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z])(?:season|series|saison|staffel|temporada|stagione)[\s\.\-_]*0*([0-9]{1,3})(?![0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    public static partial Regex MatchFolderSeasonWord();

    /// <summary>Matches a bare "S2" / "S02" folder name (whole string).</summary>
    [GeneratedRegex(@"^[Ss]0*([0-9]{1,3})$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex MatchSeasonFolderShort();

    /// <summary>
    /// Extracts a season number from a directory path's leaf folder, e.g.
    /// "/media/Show/Season 02" -> 2, "Show/S2" -> 2, "X/Season 0" -> 0. Returns
    /// <see langword="null"/> when the folder is not a season folder, so a flat
    /// show folder never spuriously supplies a season.
    /// </summary>
    public static int? TryGetFolderSeason(this string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
            return null;

        string folder = directory.LeafSegment();
        if (string.IsNullOrEmpty(folder))
            folder = directory;

        Match word = MatchFolderSeasonWord().Match(folder);
        if (word.Success)
            return int.Parse(word.Groups[1].Value);

        Match shortForm = MatchSeasonFolderShort().Match(folder);
        if (shortForm.Success)
            return int.Parse(shortForm.Groups[1].Value);

        return null;
    }

    [GeneratedRegex("/[^a-zA-Z0-9]/", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex IsAlphaNumeric();

    public static bool IsAlphaNumeric(this string str)
    {
        return IsAlphaNumeric().IsMatch(str);
    }

    [GeneratedRegex("/[0-9]/", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    public static partial Regex IsNumeric();

    public static bool IsNumeric(this string str)
    {
        return IsNumeric().IsMatch(str);
    }

    public static string PathName(this string path)
    {
        return PathSeparatorRegex().Replace(path, DirectorySeparator);
    }

    [GeneratedRegex(@"[\/\\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PathSeparatorRegex();

    public static int ToInt(this string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double d
        )
            ? (int)Math.Round(d)
            : 0;
    }

    public static int ToInt(this double value)
    {
        return Convert.ToInt32(value);
    }

    public static int ToInt(this uint value)
    {
        return Convert.ToInt32(value);
    }

    public static double ToDouble(this string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        return double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out double d
        )
            ? d
            : 0;
    }

    public static double ToDouble(this int value)
    {
        return Convert.ToDouble(value);
    }

    public static long ToLong(this string value)
    {
        if (string.IsNullOrEmpty(value))
            return 0;
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long l)
            ? l
            : 0;
    }

    public static bool ToBoolean(this string value)
    {
        if (string.IsNullOrEmpty(value))
            return false;
        return bool.TryParse(value, out bool b) && b;
    }

    public static string Spacer(string text, int padding, bool begin = false)
    {
        return begin ? SpacerBegin(text, padding) : SpacerEnd(text, padding);
    }

    private static string SpacerEnd(string text, int padding)
    {
        StringBuilder spacing = new();
        spacing.Append(text);
        for (int i = 0; i < padding - text.Length; i++)
            spacing.Append(' ');

        return spacing.ToString();
    }

    private static string SpacerBegin(string text, int padding)
    {
        StringBuilder spacing = new();
        for (int i = 0; i < padding - text.Length; i++)
            spacing.Append(' ');
        spacing.Append(text);

        return spacing.ToString();
    }

    /// <summary>
    /// Parses a Guid string. Returns <see cref="Guid.Empty"/> when the input
    /// cannot be parsed so call sites that previously crashed on a malformed
    /// id surface as a 'no match' instead. Callers that need strict
    /// validation should use <see cref="Guid.TryParse(string?, out Guid)"/>
    /// directly.
    /// </summary>
    public static Guid ToGuid(this string? id)
    {
        return Guid.TryParse(id, out Guid parsed) ? parsed : Guid.Empty;
    }

    public static string ToUtf8(this string value)
    {
        return Encoding.UTF8.GetString(Encoding.Default.GetBytes(value));
    }

    public static string SplitPascalCase(this string str)
    {
        str = PascalCaseAcronymRegex().Replace(str, "$1 $2");
        return PascalCaseWordRegex().Replace(str, "$1 $2");
    }

    [GeneratedRegex(@"(\P{Ll})(\P{Ll}\p{Ll})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PascalCaseAcronymRegex();

    [GeneratedRegex(@"(\p{Ll})(\P{Ll})", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex PascalCaseWordRegex();

    /** This method sanitizes a string by removing diacritics, non-alphanumeric characters and accents. */
    public static string Sanitize(this string str)
    {
        return str.RemoveDiacritics().RemoveNonAlphaNumericCharacters().RemoveAccents().Trim();
    }

    public static bool ContainsSanitized(this string str, string? value)
    {
        if (value == null)
            return false;

        string sanitizedStr = str.Sanitize().ToLower();
        string sanitizedValue = value.Sanitize().ToLower();

        // Sanitizing strips non-ASCII scripts (CJK/Cyrillic/Greek) down to an
        // empty string, and Contains("") trivially matches anything — fall
        // back to the original strings so a fully non-ASCII needle can't
        // produce a false match.
        if (sanitizedStr.Length == 0 || sanitizedValue.Length == 0)
        {
            string lowerStr = str.ToLower();
            string lowerValue = value.ToLower();
            return lowerStr.Contains(lowerValue) || lowerValue.Contains(lowerStr);
        }

        return sanitizedStr.Contains(sanitizedValue) || sanitizedValue.Contains(sanitizedStr);
    }

    public static bool EqualsSanitized(this string str, string value)
    {
        str = str.Sanitize().ToLower();
        value = value.Sanitize().ToLower();
        return str.Equals(value) || value.Equals(str);
    }

    public static string UrlDecode(this string str)
    {
        return WebUtility.UrlDecode(str);
    }

    public static string UrlEncode(this string str)
    {
        return WebUtility.UrlEncode(str);
    }

    public static string ToQueryUri(this string str, Dictionary<string, string>? parameters)
    {
        return str
            + (
                parameters is not null && parameters.Count > 0
                    ? "?" + string.Join("&", parameters.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty
            );
    }

    public static string EscapeQuotes(this string str)
    {
        return DoubleQuoteRegex().Replace(str, "'");
    }

    [GeneratedRegex("\"", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DoubleQuoteRegex();

    public static string Capitalize(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return char.ToUpper(str[0]) + str.Substring(1);
    }

    public static string ToTitleCase(this string str, string culture = "en-US")
    {
        if (string.IsNullOrEmpty(str))
            return str;

        TextInfo textInfo = new CultureInfo(culture, false).TextInfo;
        return textInfo.ToTitleCase(str.ToLower());
    }

    public static string ToPascalCase(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        string[] words = str.Split([' ', '_'], StringSplitOptions.RemoveEmptyEntries);
        return string.Join("_", words.Select(word => word[..1].ToUpper() + word[1..].ToLower()));
    }

    public static string ToSnakeCase(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        StringBuilder sb = new();
        for (int i = 0; i < str.Length; i++)
        {
            if (char.IsUpper(str[i]) && i > 0)
                sb.Append('_');
            sb.Append(char.ToLower(str[i]));
        }
        return sb.ToString();
    }

    public static string ToUcFirst(this string str)
    {
        if (string.IsNullOrEmpty(str))
            return str;

        return char.ToUpper(str[0]) + str[1..].ToLower();
    }

    public static int ToSeconds(this string? hms)
    {
        if (string.IsNullOrEmpty(hms))
            return 0;

        string[] rawParts = hms.Split('.')[0].Split(':');
        int[] parts = [.. rawParts.Select(part => int.TryParse(part, out int value) ? value : 0)];

        return parts.Length switch
        {
            >= 3 => (parts[0] * 60 * 60) + (parts[1] * 60) + parts[2],
            2 => (parts[0] * 60) + parts[1],
            1 => parts[0],
            _ => 0,
        };
    }

    public static int ToSeconds(this double hms)
    {
        return (int)Math.Round(hms);
    }

    public static int ToMilliSeconds(this string? hms)
    {
        return hms.ToSeconds() * 1000;
    }

    public static string ToName(this string str)
    {
        ;
        return NumberConverter.ConvertNumbersInString(str);
    }

    public static string NormalizeSearch(this string input)
    {
        if (string.IsNullOrEmpty(input))
            return string.Empty;

        // Normalize to FormD to separate characters and diacritics
        string normalized = input.Normalize(NormalizationForm.FormD);

        // Remove diacritics and normalize dashes in a single pass
        StringBuilder stringBuilder = new(normalized.Length);
        foreach (char c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            char appended = c switch
            {
                '\u2010' => '-', // Hyphen
                '\u2013' => '-', // En dash
                '\u2014' => '-', // Em dash
                '\u2212' => '-', // Minus sign
                _ => c,
            };
            stringBuilder.Append(char.ToLowerInvariant(appended));
        }

        string result = stringBuilder.ToString();

        // Remove non-alphanumeric characters (optional)
        result = NonAlphaNumericKeepDashRegex().Replace(result, "");

        return result;
    }

    [GeneratedRegex(@"[^a-zA-Z0-9\s-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAlphaNumericKeepDashRegex();

    public enum TextDirection
    {
        LTR,
        RTL,
    }

    public static TextDirection GetTextDirection(this string str)
    {
        // Check for presence of RTL characters
        foreach (char c in str)
        {
            if (
                c is >= '\u0590' and <= '\u05FF'
                || // Hebrew
                c is >= '\u0600' and <= '\u06FF'
                || // Arabic
                c is >= '\u0750' and <= '\u077F'
                || // Arabic Supplement
                c is >= '\u08A0' and <= '\u08FF'
                || // Arabic Extended-A
                c is >= '\uFB50' and <= '\uFDFF'
                || // Arabic Presentation Forms-A
                c is >= '\uFE70' and <= '\uFEFF'
            ) // Arabic Presentation Forms-B
            {
                return TextDirection.RTL;
            }
        }
        return TextDirection.LTR;
    }

    [GeneratedRegex(@"\s", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex SingleWhitespaceRegex();

    [GeneratedRegex(@"[^a-z0-9\s-_]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex InvalidSlugCharsRegex();

    [GeneratedRegex(@"([-_]){2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RepeatedSlugSeparatorRegex();

    public static string ToSlug(this string value)
    {
        //First to lower case
        value = value.ToLowerInvariant();

        //Remove all accents
        byte[] bytes = Encoding.GetEncoding("ISO-8859-1").GetBytes(value);
        value = Encoding.ASCII.GetString(bytes);

        //Replace spaces
        value = SingleWhitespaceRegex().Replace(value, "-");

        //Remove invalid chars
        value = InvalidSlugCharsRegex().Replace(value, "");

        //Trim dashes from end
        value = value.Trim('-', '_');

        //Replace double occurences of - or _
        value = RepeatedSlugSeparatorRegex().Replace(value, "$1");

        // random id
        value += "-" + Guid.NewGuid().ToString("n").Substring(0, 8);

        return value;
    }
}
