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
using MovieFileLibrary;
using NoMercy.NmSystem.Domain;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.MediaProcessing.Files.Parsing;

/// <summary>
/// Everything the file list knows about a name before a provider is asked:
/// which adapter claimed it, the year, and the season/episode the folder or a
/// trailing number fills in.
/// <para>
/// The local and driver-aware list paths ran their own copy of these rules, and
/// a copy is a place for them to drift — the "take the season from a Season N
/// folder" rule reached one of them a release after the other. One resolver, one
/// set of rules, and the corpus tests exercise what both paths actually run.
/// </para>
/// </summary>
public sealed partial class FilenameResolver(IFilenameParserPipeline pipeline)
{
    /// <param name="fileNameWithExtension">Name only, no directory.</param>
    /// <param name="directoryName">The directory the file sits in, for the "Season N folder" and folder-title rules.</param>
    /// <param name="pathForTitle">The full path the adapters use to seed an unmatched title.</param>
    public ResolvedName Resolve(
        string fileNameWithExtension,
        string? directoryName,
        string pathForTitle,
        string libraryType
    )
    {
        string rawFileName = Path.GetFileNameWithoutExtension(fileNameWithExtension);

        // A [tmdb-1234] hint baked into the name, e.g. "[tmdb-553604]Spring (2019).mkv".
        int? overrideTmdbId = rawFileName.TryGetTmdbHint();

        string cleanedForYear = StringExtensions
            .RemoveBracketedString()
            .Replace(rawFileName, string.Empty);
        string? extractedYear = cleanedForYear.TryGetYear();

        string title = StringExtensions
            .RemoveBracketedString()
            .Replace(pathForTitle.Replace("v2", ""), string.Empty);

        // Everything the release put in brackets or parentheses goes before any
        // adapter reads a number out of this. Only the square brackets used to
        // be stripped, so the parenthesised technical block stayed — and it is
        // full of digits. "H2O ~FOOTPRINTS IN THE SAND~ 03 RAW (1280x720
        // DivX6.61).avi" was filed as episode 61, taken from the codec version;
        // "Hatenkou Yuugi 03 RAW (D-KBS_704x396 24fps DivX6.7).avi" as episode
        // 7. The real episode number was sitting in plain sight before the
        // word RAW in both.
        //
        // The year is already out (TryGetYear ran on its own copy above), so
        // dropping "(2019)" here costs nothing.
        string withoutGroups = ReleaseGroup().Replace(rawFileName, RemoveUnlessItNamesTheEpisode);

        // A group the extension strip cut the closing half off. Left in, its
        // digits are still on the table for whichever adapter looks first.
        int unclosedGroup = FirstUnclosedGroup(withoutGroups);
        if (unclosedGroup > 0)
            withoutGroups = withoutGroups[..unclosedGroup];

        string cleanedFileName = VersionSuffix().Replace(withoutGroups, string.Empty).Trim();

        // The tracker that repackaged a release stamps its address on the front:
        // "www.Torrenting.com.-.Show.Name.S01E02". The show is everything after
        // it, and left on, the search is for a name no catalogue holds.
        //
        // An address is matched by BEING one rather than by being on a list. The
        // parsers this corpus came from kept sixty-odd literal tracker tags and
        // needed a new entry for every site that ever appeared; the ones written
        // in brackets are already gone with every other bracket, and what
        // survives is exactly the unbracketed address.
        cleanedFileName = LeadingSeparator()
            .Replace(TrackerAddress().Replace(cleanedFileName, string.Empty), string.Empty);

        MovieFile parsed = pipeline.Parse(
            new()
            {
                FileNameWithExtension = fileNameWithExtension,
                DirectoryName = directoryName,
                Title = title,
                CleanedFileName = cleanedFileName,
                FolderTitle = TitleFromFolder(directoryName),
                LibraryType = libraryType,
            }
        );

        parsed.Year = extractedYear ?? parsed.Year;
        if (parsed.Title is null)
            return new(parsed, overrideTmdbId, SeasonExplicit: false, AirDate: null);

        parsed.Title = StringExtensions
            .RemoveParenthesizedString()
            .Replace(parsed.Title, string.Empty)
            .Trim();

        // The episode marker is not part of the show's name. Left on, the title
        // that goes to a provider is "Never-Ending Summer - S01E19" — a search
        // string no catalogue holds, answered with whatever scores least badly.
        // The number-only shape ("- 175") was already trimmed; this is the same
        // rule for the shape that spells the season out.
        Match episodeMarker = TitleEpisodeMarker().Match(parsed.Title);
        if (episodeMarker is { Success: true, Index: > 0 })
            parsed.Title = parsed.Title[..episodeMarker.Index].TrimEnd(' ', '-', '.', '_');

        // An opening bracket with nothing closing it. The paired-group strip
        // cannot touch it, so the whole tail rides into the title:
        // "H2O ~FOOTPRINTS IN THE SAND~ 03 RAW (1280x720 DivX6.61).avi" was
        // searched for as "...RAW (1280x720 DivX6", because dropping the
        // extension cut the closing paren off first.
        int unclosed = FirstUnclosedGroup(parsed.Title);
        if (unclosed > 0)
            parsed.Title = parsed.Title[..unclosed].Trim();

        // A creditless marker sitting mid-title, where the show's name IS in
        // the file: "Air TV - Creditless Opening (DVD)(AT)", "Kanon - Fan DVD -
        // Full Opening (GFE)". The show is everything before the marker, and
        // everything from it on describes the extra.
        //
        // Only the unambiguous release words are cut on. Bare "Opening",
        // "Ending" and "Menu" are NOT in here on purpose — Opening Act, Great
        // British Menu and Never-Ending Summer are real shows, and this rule
        // would rename them.
        Match creditless = CreditlessMarkerInTitle().Match(parsed.Title);
        if (creditless is { Success: true, Index: > 0 })
            parsed.Title = parsed.Title[..creditless.Index].TrimEnd(' ', '-', '.', '_', ',');

        // Release vocabulary that survived to the end of the title: the source,
        // the codec, the resolution, the language tag. Measured against 10,329
        // real release names, 253 titles still carried one — "CLANNAD 06 raw",
        // "Inukami Creditless Ending HKG DVD" — and every one is a search string
        // with a word in it no catalogue has ever heard the show called.
        parsed.Title = TrailingReleaseNoise().Replace(parsed.Title, string.Empty).Trim();

        // The episode number, still on the end of the title once the group that
        // used to follow it is gone: "Show Name - 12".
        //
        // Only when the name never spelled out a season anywhere. "Psycho-Pass
        // 2 - S01E02" says S01E02 out loud, so its trailing 2 belongs to the
        // show and matching the episode number is a coincidence — stripping it
        // renamed the show. A name with no marker anywhere else is the one
        // whose trailing number IS the marker.
        if (
            !TitleEpisodeMarker().IsMatch(rawFileName) && AbsoluteEpisodeForm().IsMatch(rawFileName)
        )
        {
            Match trailingEpisode = TrailingNumber().Match(parsed.Title);

            if (trailingEpisode.Success)
                parsed.Title = parsed.Title[..trailingEpisode.Index].TrimEnd(' ', '-', '.', '_');
        }

        // A bracket with no partner takes its debris into the title. Real
        // example: "AW-Raw]_One_Piece_310_SD.WS_(704x396)[6AF865BD].avi" was
        // searched for as "AW-Raw] One Piece", because only balanced pairs are
        // stripped and this one opens outside the name entirely.
        int orphanClose = parsed.Title.IndexOfAny([']', ')', '}']);
        if (orphanClose >= 0 && parsed.Title.IndexOfAny(['[', '(', '{']) > orphanClose)
            parsed.Title = parsed.Title[(orphanClose + 1)..].Trim();
        else if (orphanClose >= 0 && !parsed.Title.Contains('[') && !parsed.Title.Contains('('))
            parsed.Title = parsed.Title[(orphanClose + 1)..].Trim();

        // A file named for its ROLE rather than its show carries no title at
        // all. "Opening 06", "Ending 18", "Menu - 01", "NCED - 01",
        // "OVA-Water Slider" — every one of those is a word describing what the
        // file is inside a release, and the show name is in the folder holding
        // it.
        //
        // Handing that word to a provider as though it were a title is how
        // "Ending 18.mkv" became episode 19 of Never-Ending Summer, "Menu -
        // 01.mkv" became Great British Menu, "Opening 06.mkv" became an episode
        // of Opening Act, and "NCED - 01.mkv" became Flavor, NC. A search will
        // always return something; that is exactly why it must not be asked.
        if (IsRoleWordRatherThanTitle(parsed.Title))
        {
            string folderTitle = TitleFromFolder(directoryName);

            // No title in the name and none in the folder either means nobody
            // knows what this is. Unmatched is a file the operator can place;
            // a confident wrong answer is one they have to find first.
            parsed.Title = folderTitle.Length > 0 ? folderTitle : null;
        }

        // Whether the season came from the name or was defaulted, which is what
        // decides if the absolute-index fallback stays available downstream.
        bool seasonExplicit = parsed.Season.HasValue;

        // Dated daily episode (yyyy.mm.dd): the air date is the key, so any stray
        // number that would be misread as an episode goes. The resolver maps the
        // date to the episode that aired that day.
        //
        // A name carrying BOTH — "Show Name - S06E01 - 2009-12-20 - Ep Name" —
        // is not a daily episode. The date is the air date printed in the title
        // and the marker is the key, so reading the date as the key threw S06E01
        // away and the file arrived unmatched.
        DateOnly? airDate =
            libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            && !TitleEpisodeMarker().IsMatch(rawFileName)
            && !StringExtensions.MatchCrossFormatEpisode().IsMatch(rawFileName)
                ? DailyEpisodeParser.TryGetAirDate(rawFileName)
                : null;
        if (airDate.HasValue)
        {
            parsed.Season = null;
            parsed.Episode = null;
            return new(parsed, overrideTmdbId, seasonExplicit, airDate);
        }

        // A raw disc rip is named for its track ("The Pink Panther Volume 1_t12"),
        // and a track index is not an episode. The only other number in the name
        // is the volume, so every track on a disc was claiming that volume's
        // first episode — 29 files on two slots. Nothing here is knowable, so
        // nothing is guessed and the file goes to the operator to place.
        if (DiscTrackMarker().IsMatch(cleanedFileName))
        {
            parsed.Season = null;
            parsed.Episode = null;
            return new(parsed, overrideTmdbId, seasonExplicit, airDate);
        }

        // A creditless opening or ending — NCOP, NCED, and the textless and
        // "clean" spellings of the same thing — is the sequence with the credits
        // taken off, not an episode. It carries no episode number of its own, so
        // the only numbers in the name belong to the season and the version, and
        // every one of them was read as episode one: a show's NCOP and its NCED
        // both landed on S00E01 and each overwrote the other.
        if (CreditlessSequenceMarker().IsMatch(cleanedFileName))
        {
            parsed.Season = null;
            parsed.Episode = null;
            return new(parsed, overrideTmdbId, seasonExplicit, airDate);
        }

        // Plex-style layout: a file that omits the season but lives in a
        // "Season N" folder takes the season from the folder rather than
        // defaulting to 1. seasonExplicit deliberately keeps the name-derived
        // value so the absolute-index fallback stays available.
        int? folderSeason = directoryName.TryGetFolderSeason();

        if (parsed is { Episode: not null, Season: null })
            parsed.Season = folderSeason ?? 1;

        // A name with no episode marker has no episode. This used to reach into
        // the TITLE for the first number it could find and call that the episode,
        // which is where every mismatch this parser has produced came from: a
        // creditless opening and its matching ending both landing on episode one
        // and overwriting each other, a disc's tracks all claiming their volume's
        // first episode, a show's specials collapsing onto the same slot. The
        // titles alone would have been enough to distrust it — "Mob Psycho 100"
        // is not episode 100 and "86" is not episode 86.
        //
        // A real bare-number episode ("Fairy Tail - 175") never reached here;
        // the absolute-index adapter claims those, and this ran only once every
        // adapter had already declined. So there was nothing left for it to be
        // right about, and a file it cannot place now says so and goes to the
        // operator instead of quietly taking someone else's slot.
        return new(parsed, overrideTmdbId, seasonExplicit, airDate);
    }

    /// <summary>
    /// Words that describe a file's place in a release rather than name a show:
    /// the creditless sequences, the disc furniture, the promos and the extras.
    /// <para>Deliberately matched only when the word is ALL that is left of the
    /// title. "Opening Act" and "Great British Menu" are real shows and stay
    /// real shows; it is the file called nothing but <c>Opening 06</c> that has
    /// no title to give.</para>
    /// </summary>
    private static readonly HashSet<string> RoleWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "op",
        "ed",
        "opening",
        "ending",
        "ncop",
        "nced",
        "nc",
        "creditless",
        "textless",
        "clean",
        "ova",
        "oav",
        "oad",
        "menu",
        "preview",
        "previews",
        "trailer",
        "trailers",
        "promo",
        "promos",
        "pv",
        "cm",
        "sp",
        "special",
        "specials",
        "extra",
        "extras",
        "bonus",
        "credits",
        "intro",
        "outro",
        "teaser",
        "recap",
        "digest",
        "interview",
        "commentary",
        "disc",
        "disk",
        "part",
        "chapter",
        "title",
        "untitled",
        "video",
        "track",
    };

    /// <summary>
    /// Whether <paramref name="title"/> is a role word (or nothing but numbers)
    /// once the numbering and separators a release puts around it are removed.
    /// </summary>
    private static bool IsRoleWordRatherThanTitle(string title)
    {
        // Not one letter or digit anywhere. Real corpus case: a directory
        // listing's tree drawing survived as the whole title —
        // "│      [00][SG][ARIA_The_Animation][00][XVID][BIG5].avi" searched for
        // "│", because the show's actual name only ever appears inside brackets
        // and every bracket had been stripped. 41 files in one archive.
        if (!title.Any(char.IsLetterOrDigit))
            return true;

        string stripped = RoleWordNoise().Replace(title, " ").Trim();

        if (stripped.Length == 0)
            return true;

        string[] words = stripped.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        return words.Length > 0 && words.All(RoleWords.Contains);
    }

    /// <summary>
    /// The numbering and punctuation a release wraps a role word in — the
    /// "- 01" of "NCED - 01", the "06" of "Opening 06", the hyphen in
    /// "OVA-Water Slider".
    /// </summary>
    [GeneratedRegex(@"[\d\.\-_\[\]\(\)#]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex RoleWordNoise();

    /// <summary>
    /// An explicit season/episode marker sitting inside what was taken for the
    /// title. Anything from there on describes the episode, not the show.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S[0-9]{1,4}[\s._-]*E[0-9]{1,4}(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TitleEpisodeMarker();

    /// <summary>
    /// Drops a bracketed or parenthesised group unless the group is where the
    /// release put the season and episode.
    /// <para>Most groups are the technical block — resolution, codec, source,
    /// CRC — and every one of them is full of digits an adapter would happily
    /// read as an episode. But "[Judas] Blue Lock - 05 (S01E05) [1080p]" puts
    /// the marker itself in parentheses, so removing them all threw the answer
    /// away along with the noise.</para>
    /// <para>The cross-format spelling of the same marker, "Show Name [05x12] Ep
    /// Name", is kept only when the group is not the FIRST thing in the name.
    /// The leading group is the fansub tag, and a tag reads as a marker often
    /// enough to matter: "[3x3] Spice and Wolf - 01 RAW" would be season 3
    /// episode 3 of Spice and Wolf, which is neither the show's numbering nor
    /// the episode in the file.</para>
    /// </summary>
    private static string RemoveUnlessItNamesTheEpisode(Match group)
    {
        if (TitleEpisodeMarker().IsMatch(group.Value))
            return group.Value;

        return group.Index > 0 && StringExtensions.MatchCrossFormatEpisode().IsMatch(group.Value)
            ? group.Value
            : string.Empty;
    }

    /// <summary>A bracketed or parenthesised block, innermost pair only.</summary>
    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.None,
        pattern: @"\[[^\[\]]*\]|\([^()]*\)|\{[^{}]*\}"
    )]
    private static partial Regex ReleaseGroup();

    /// <summary>
    /// A web address at the very front, with the punctuation a tracker packs
    /// around it.
    /// <para>The <c>www.</c> is required, and that is the whole guard. A bare
    /// host pattern would read "Back.to.the.Future" as the domain "Back.to" and
    /// cut the show's name in half — plenty of titles are dotted words ending in
    /// something that is also a top-level domain.</para>
    /// </summary>
    [GeneratedRegex(
        @"^[\s.\-_]*www\.[A-Za-z0-9.\-]+?\.[A-Za-z]{2,6}(?![A-Za-z0-9])[\s.\-_]*",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TrackerAddress();

    /// <summary>
    /// The " - " a tracker leaves behind once its bracketed address is gone.
    /// <para>Whitespace and a dash only, so this rule adds nothing to what
    /// already happens to a leading dot further down.</para>
    /// </summary>
    [GeneratedRegex(@"^\s*[-–—]?\s*", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex LeadingSeparator();

    /// <summary>
    /// The absolute-episode form a fansub release uses: the show, a spaced
    /// dash, then the number. Its presence is what makes a trailing number in
    /// the title an episode marker rather than part of the name.
    /// </summary>
    [GeneratedRegex(@"[\s._]-[\s._][0-9]{1,4}(?![0-9])", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex AbsoluteEpisodeForm();

    /// <summary>A number sitting at the very end of a title, with the separator
    /// a release puts before it.</summary>
    [GeneratedRegex(@"[\s._-]+(?<number>[0-9]{1,4})\s*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex TrailingNumber();

    /// <summary>
    /// Index of an opening bracket that is never closed, or -1 when every group
    /// is balanced. Only the outermost unclosed one matters: everything from it
    /// to the end of the string was meant to be a group and is not a title.
    /// </summary>
    private static int FirstUnclosedGroup(string title)
    {
        int depth = 0;
        int firstOpenAtTopLevel = -1;

        for (int index = 0; index < title.Length; index++)
        {
            char current = title[index];

            if (current is '(' or '[' or '{')
            {
                if (depth == 0)
                    firstOpenAtTopLevel = index;
                depth++;
            }
            else if (current is ')' or ']' or '}')
            {
                if (depth > 0)
                    depth--;
            }
        }

        return depth > 0 ? firstOpenAtTopLevel : -1;
    }

    /// <summary>
    /// The release words that mean "this is the sequence without its credits".
    /// Unambiguous by design: no show is called Creditless or Textless, so
    /// cutting the title here cannot rename a real one.
    /// </summary>
    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.IgnoreCase,
        pattern: @"(?<![A-Za-z0-9])(?:creditless|textless|non-?credit(?:s|less)?|"
            + @"NC(?:OP|ED)|clean[\s._-]+(?:OP|ED|opening|ending))(?![A-Za-z0-9])"
    )]
    private static partial Regex CreditlessMarkerInTitle();

    /// <summary>
    /// Release vocabulary clinging to the end of a title — source, codec,
    /// resolution, language. Anchored to the end and allowed to repeat, so
    /// "Inukami Creditless Ending HKG DVD" loses the tail without "Air TV"
    /// losing the TV that is part of the show's name.
    /// </summary>
    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.IgnoreCase,
        pattern: @"(?:[\s._-]+(?:raw|dvd|dvdrip|bd|bdrip|blu-?ray|hdtv|web-?rip|web-?dl|ts|"
            + @"x26[45]|h\.?26[45]|hevc|avc|xvid|divx|aac|flac|ac3|dts|mp3|"
            + @"[0-9]{3,4}p|[0-9]{3,4}x[0-9]{3,4}|hi10p?|10bit|8bit|"
            + @"jap|jpn|eng|ita|multi|dual[\s._-]*audio|sub(?:bed|s)?|dub(?:bed|s)?|"
            + @"uncensored|censored|remux|batch|complete))+\s*$"
    )]
    private static partial Regex TrailingReleaseNoise();

    internal static string TitleFromFolder(string? directoryName)
    {
        // A "Season 2" folder names a season, not a show. In the standard layout
        // the show is the folder above it, and a file that leans on its folder
        // for a title — "Season 2/S02E06 The Tower is Tall.mkv" — was handed
        // "Season 2" as the show's name and searched for under it.
        if (directoryName.TryGetFolderSeason().HasValue)
            directoryName = directoryName?.ParentSegment();

        string folderName = directoryName?.LeafSegment() ?? "";
        if (string.IsNullOrWhiteSpace(folderName))
            return "";

        string cleaned = StringExtensions.RemoveBracketedString().Replace(folderName, string.Empty);
        cleaned = StringExtensions.RemoveParenthesizedString().Replace(cleaned, string.Empty);

        Match seasonTag = StringExtensions.MatchSeasonTag().Match(cleaned);
        if (seasonTag is { Success: true, Index: > 0 })
            cleaned = cleaned[..seasonTag.Index];

        string folderTitle = cleaned
            .Replace('.', ' ')
            .Replace('_', ' ')
            .TrimEnd('-', '.', '_', ' ')
            .Trim();

        // The year is captured separately by TryGetYear, so a trailing one here
        // is noise in the title.
        Match yearInFolder = StringExtensions.MatchYearRegex().Match(folderTitle);
        if (yearInFolder.Success)
            folderTitle = folderTitle[..yearInFolder.Index].TrimEnd('-', '.', '_', ' ');

        return folderTitle;
    }

    /// <summary>
    /// A re-release marks its version on the episode number itself — "Dororo
    /// (2019) - 01v2", "Detective Conan - 678v2". The suffix glues to the digits,
    /// so every matcher that wants a standalone number rejected both halves and
    /// the episode was lost entirely. Anchored to a preceding digit, so a title
    /// that merely contains a v ("NieR-Automata Ver1.1a") is untouched.
    /// </summary>
    [GeneratedRegex(@"(?<=[0-9])v[0-9]{1,2}(?![A-Za-z0-9])", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex VersionSuffix();

    /// <summary>A MakeMKV-style disc track suffix — "…Volume 1_t12".</summary>
    [GeneratedRegex(@"_t[0-9]{2}$", RegexOptions.IgnoreCase, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DiscTrackMarker();

    /// <summary>
    /// A creditless opening or ending. NC is the common prefix (NCOP / NCED),
    /// and the same sequences ship as "Textless OP" or "Clean ED" often enough
    /// to be worth matching. The boundaries are what keep this off real titles:
    /// without them "NCOP" matches inside a word and "clean" matches any show
    /// with Clean in its name.
    /// </summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:NC(?:OP|ED)|(?:textless|clean|creditless)[\s\._-]*(?:OP|ED|opening|ending))(?![A-Za-z])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CreditlessSequenceMarker();
}

/// <param name="OverrideTmdbId">A [tmdb-1234] hint from the name, which outranks any search.</param>
/// <param name="SeasonExplicit">The season came from the name, not from a folder or a default.</param>
public readonly record struct ResolvedName(
    MovieFile Parsed,
    int? OverrideTmdbId,
    bool SeasonExplicit,
    DateOnly? AirDate
);
