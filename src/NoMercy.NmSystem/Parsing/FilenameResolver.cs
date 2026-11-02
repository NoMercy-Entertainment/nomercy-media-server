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

        string cleanedForYear = TechnicalSize()
            .Replace(
                StringExtensions.RemoveBracketedString().Replace(rawFileName, string.Empty),
                RemoveIfPictureSize
            );
        // A year in parentheses is the release year: "Blade Runner 2049 (2017)".
        string? extractedYear =
            ParenthesizedYear().Matches(cleanedForYear).LastOrDefault()?.Groups["year"].Value
            ?? BareYear().Matches(cleanedForYear).LastOrDefault()?.Value
            ?? cleanedForYear.TryGetYear()
            ?? NullIfEmpty(BracketedYear().Match(rawFileName).Groups["year"].Value)
            ?? ParenthesizedYear()
                .Matches(directoryName ?? "")
                .LastOrDefault()
                ?.Groups["year"]
                .Value
            ?? NullIfEmpty(
                FolderBareYear()
                    .Match(directoryName?.LeafSegment() ?? string.Empty)
                    .Groups["year"]
                    .Value
            );

        string title = StringExtensions
            .RemoveBracketedString()
            .Replace(pathForTitle.Replace("v2", ""), string.Empty);
        title = TrackerAddress().Replace(title, string.Empty);
        title = TechnicalSize().Replace(title, RemoveIfPictureSize);

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
        // it, and left on, the search is for a name no catalog holds.
        //
        // An address is matched by BEING one rather than by being on a list. The
        // parsers this corpus came from kept sixty-odd literal tracker tags and
        // needed a new entry for every site that ever appeared; the ones written
        // in brackets are already gone with every other bracket, and what
        // survives is exactly the unbracketed address.
        cleanedFileName = LeadingSeparator()
            .Replace(TrackerAddress().Replace(cleanedFileName, string.Empty), string.Empty);

        // Sizes and codec numbers are not episodes: "704x480", "h.264". A letter
        // after an episode ("01b", "111C") marks a part of that episode.
        cleanedFileName = TechnicalSize().Replace(cleanedFileName, RemoveIfPictureSize);
        cleanedFileName = LetteredEpisode().Replace(cleanedFileName, "${n}");
        bool fractionalEpisode = FractionalEpisode().IsMatch(cleanedFileName);

        MovieFile parsed = pipeline.Parse(
            new()
            {
                FileNameWithExtension = TrackerAddress()
                    .Replace(
                        TechnicalSize().Replace(fileNameWithExtension, RemoveIfPictureSize),
                        string.Empty
                    ),
                DirectoryName = directoryName,
                Title = title,
                CleanedFileName = cleanedFileName,
                FolderTitle = TitleFromFolder(directoryName),
                LibraryType = libraryType,
            }
        );

        // A show's year is its first-air year; an episode's air date would send
        // the provider search to the wrong year, so only movies read one.
        parsed.Year =
            extractedYear
            ?? parsed.Year
            ?? (
                libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
                    ? null
                    : YearFromDate(rawFileName)
            );

        // The library stores whole episodes; "07.5" is a special between 7 and 8.
        if (fractionalEpisode)
            parsed.Episode = null;

        // A batch "01-04" or "01..03" is stored under the first episode it lists.
        if (
            !TitleEpisodeMarker().IsMatch(rawFileName)
            && libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
        )
        {
            Match batch = BatchRange().Match(rawFileName);
            int rangeEnd = batch.Success
                ? int.Parse(batch.Groups["b"].Value, CultureInfo.InvariantCulture)
                : 0;
            int rangeStart = batch.Success
                ? int.Parse(batch.Groups["a"].Value, CultureInfo.InvariantCulture)
                : 0;
            bool inLaterRange =
                batch.Success
                && parsed.Episode is int current
                && BatchRange()
                    .Matches(rawFileName)
                    .Skip(1)
                    .Any(later =>
                        int.Parse(later.Groups["a"].Value, CultureInfo.InvariantCulture) == current
                        || int.Parse(later.Groups["b"].Value, CultureInfo.InvariantCulture)
                            == current
                    );
            if (
                rangeEnd is > 1 and < 1900
                && rangeEnd > rangeStart
                && (
                    parsed.Episode is null
                    || inLaterRange
                    || parsed.Episode == rangeEnd
                    || (
                        batch.Index > 0
                        && rawFileName[batch.Index - 1] is '[' or '('
                        && parsed.Episode != rangeStart
                    )
                )
            )
                parsed.Episode = rangeStart;

            // "(Season 1)" and "2nd Season" print the season outside any episode marker.
            Match seasonWord = SeasonWordBeforeEpisode().Match(rawFileName);
            bool spelledSeason =
                seasonWord.Groups["n"].Success
                && seasonWord.Value.Length > 1
                && !char.IsDigit(seasonWord.Value[1]);
            if (seasonWord.Success && (parsed.Season is null or 0 or 1 || spelledSeason))
            {
                string digits = seasonWord.Groups["n"].Success
                    ? seasonWord.Groups["n"].Value
                    : seasonWord.Groups["n2"].Value;
                parsed.Season = int.Parse(digits, CultureInfo.InvariantCulture);
            }
            else if (
                seasonWord is { Success: true, Groups: { } groups }
                && groups["n2"].Success
                && parsed.Episode is null
                && parsed.Season > int.Parse(groups["n2"].Value, CultureInfo.InvariantCulture)
            )
            {
                // "2nd Season 24": the number after the season word is the episode.
                parsed.Episode = parsed.Season;
                parsed.Season = int.Parse(groups["n2"].Value, CultureInfo.InvariantCulture);
            }

            if (
                parsed.Episode is null
                && ParenthesizedEpisodeAtEnd().Match(rawFileName) is { Success: true } inParens
                && !rawFileName[..inParens.Index].Trim().All(char.IsDigit)
            )
                parsed.Episode = int.Parse(
                    inParens.Groups["n"].Value,
                    CultureInfo.InvariantCulture
                );

            if (
                parsed.Episode is null
                && BracketedEpisode().Match(rawFileName) is { Success: true } inBrackets
            )
                parsed.Episode = int.Parse(
                    inBrackets.Groups["n"].Value,
                    CultureInfo.InvariantCulture
                );

            if (
                JapaneseSeason().Match(rawFileName) is { Success: true } period
                && parsed.Season is null or 0 or 1
            )
                parsed.Season = int.Parse(period.Groups["n"].Value, CultureInfo.InvariantCulture);

            if (
                !TitleEpisodeMarker().IsMatch(rawFileName)
                && DashedEpisodeInMiddle().Match(cleanedFileName) is { Success: true } middle
                && int.Parse(middle.Groups["n"].Value, CultureInfo.InvariantCulture)
                    != parsed.Episode
            )
                parsed.Episode = int.Parse(middle.Groups["n"].Value, CultureInfo.InvariantCulture);

            if (
                parsed.Episode is null
                && VersionedEpisode().Match(rawFileName) is { Success: true } versioned
            )
                parsed.Episode = int.Parse(
                    versioned.Groups["n"].Value,
                    CultureInfo.InvariantCulture
                );

            if (SpecialEpisode().Match(cleanedFileName) is { Success: true } special)
                parsed.Episode = int.Parse(special.Groups["n"].Value, CultureInfo.InvariantCulture);

            // "Another OVA - The Other": a special with no number must not take episode 1's slot.
            if (
                parsed.Episode is not null
                && UnnumberedSpecial().IsMatch(cleanedFileName)
                && !cleanedFileName.Any(char.IsDigit)
            )
                parsed.Episode = null;

            // "[全24集]" counts the episodes of a complete season; it names none.
            if (CompleteSeasonCount().IsMatch(rawFileName))
                parsed.Episode = null;
        }

        if (
            (
                parsed.Episode is null
                || (parsed.Episode == 1 && SeasonWordBeforeEpisode().IsMatch(rawFileName))
            )
            && libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            && !TitleEpisodeMarker().IsMatch(rawFileName)
            && DashedEpisodeAtEnd().Match(cleanedFileName) is { Success: true } dashed
        )
            parsed.Episode = int.Parse(dashed.Groups["n"].Value, CultureInfo.InvariantCulture);

        if (libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType)
            ApplyExplicitMarker(parsed, cleanedFileName, rawFileName, directoryName);
        else if (
            libraryType is MediaTypes.MovieMediaType
            && parsed.Episode is not null
            && MovieEpisodeWord().IsMatch(cleanedFileName)
            && !TitleEpisodeMarker().IsMatch(rawFileName)
        )
        {
            // "Star Wars Episode 4 A New Hope" is a movie whose title says Episode.
            int cut = cleanedFileName.Length;
            foreach (
                Match tag in new[]
                {
                    ReleaseTokenInTitle().Match(cleanedFileName),
                    BareYear().Match(cleanedFileName),
                }
            )
            {
                if (tag.Success && tag.Index > 0)
                    cut = Math.Min(cut, tag.Index);
            }

            parsed.Title = cleanedFileName[..cut]
                .Replace('.', ' ')
                .Replace('_', ' ')
                .Trim(' ', '-');
            parsed.Episode = null;
            parsed.Season = null;
        }

        if (
            libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            && SeasonDisc().Match(rawFileName) is { Success: true } disc
        )
        {
            parsed.Season = int.Parse(disc.Groups["n"].Value, CultureInfo.InvariantCulture);
            parsed.Episode = null;
        }
        else if (
            libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType
            && parsed.Episode is null
            && SpelledSeason().Match(rawFileName) is { Success: true } spelled
            && SeasonNumberFromWord(spelled.Groups["w"].Value) is int spelledNumber
        )
            parsed.Season = spelledNumber;

        // A pack that lists seasons ("S01 S02", "Seasons 1-2", "S01D03") belongs
        // to the first one listed, never to season 0.
        if (parsed.Episode is null || parsed.Season == 0)
        {
            Match firstSeason = FirstListedSeason().Match(rawFileName);
            if (
                !firstSeason.Success
                && (
                    parsed.Episode is null
                    || rawFileName.Contains("complete", StringComparison.OrdinalIgnoreCase)
                )
            )
                firstSeason = GluedSeasonList().Match(rawFileName);
            if (firstSeason.Success)
            {
                parsed.Episode = null;
                parsed.Season = int.Parse(
                    firstSeason.Groups["n"].Value,
                    CultureInfo.InvariantCulture
                );
            }
        }

        // "2012.AC3.720p.BluRay" and "24.S02E02": the number is the title, and
        // every adapter took it for a year or an episode and left nothing.
        bool numberTitle = false;
        if (parsed.Title is null || !parsed.Title.Any(char.IsLetterOrDigit))
        {
            Match leading = LeadingNumberTitle().Match(rawFileName);
            if (leading.Success)
            {
                parsed.Title = leading.Groups["t"].Value;
                numberTitle = NumberIsFollowedByTags(rawFileName, leading);
                if (parsed.Year == parsed.Title)
                    parsed.Year = null;
            }
            else
            {
                parsed.Title = TitleFromBrackets(rawFileName);
            }
        }

        if (parsed.Title is null)
            return new(parsed, overrideTmdbId, SeasonExplicit: false, AirDate: null);

        parsed.Title = StringExtensions
            .RemoveParenthesizedString()
            .Replace(parsed.Title, string.Empty)
            .Trim();

        // The episode marker is not part of the show's name. Left on, the title
        // that goes to a provider is "Never-Ending Summer - S01E19" — a search
        // string no catalog holds, answered with whatever scores least badly.
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

        // Source, codec, resolution and language tags no title contains. The
        // show's name is everything before the first one.
        Match releaseToken = ReleaseTokenInTitle().Match(parsed.Title);
        if (releaseToken is { Success: true, Index: > 0 })
            parsed.Title = parsed.Title[..releaseToken.Index].TrimEnd(' ', '-', '.', '_', ',');

        // Release vocabulary that survived to the end of the title: the source,
        // the codec, the resolution, the language tag. Measured against 10,329
        // real release names, 253 titles still carried one — "CLANNAD 06 raw",
        // "Inukami Creditless Ending HKG DVD" — and every one is a search string
        // with a word in it no catalog has ever heard the show called.
        parsed.Title = TrailingReleaseNoise().Replace(parsed.Title, string.Empty).Trim();
        parsed.Title = LeadingTrackerWords().Replace(parsed.Title, string.Empty).Trim();

        // "Part 2", "Vol.03" and "Disk 2 of 2" are the pack's furniture for a series, but a movie title.
        if (libraryType is MediaTypes.TvMediaType or MediaTypes.AnimeMediaType)
            parsed.Title = TvPackNoise().Replace(parsed.Title, string.Empty).Trim();

        // "2012.AC3.720p" and "1923.s01e01": the noise strip emptied a title the
        // adapters had seeded from the tail, and the leading number is the show.
        if (
            !parsed.Title.Any(char.IsLetterOrDigit)
            && LeadingNumberTitle().Match(rawFileName) is { Success: true } named
        )
        {
            parsed.Title = named.Groups["t"].Value;
            numberTitle = NumberIsFollowedByTags(rawFileName, named);
            if (parsed.Year == parsed.Title)
                parsed.Year = null;
        }

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
        if (
            !numberTitle
            && LeadingNumberTitle().Match(rawFileName) is { Success: true } lead
            && lead.Groups["t"].Value == parsed.Title
        )
            numberTitle = NumberIsFollowedByTags(rawFileName, lead);

        if (
            !numberTitle
            && (parsed.Year is null || !StringExtensions.MatchYearRegex().IsMatch(rawFileName))
            && IsRoleWordRatherThanTitle(parsed.Title)
        )
        {
            string folderTitle = TitleFromFolder(directoryName);

            // No title in the name and none in the folder either means nobody
            // knows what this is. Unmatched is a file the operator can place;
            // a confident wrong answer is one they have to find first.
            parsed.Title = folderTitle.Length > 0 ? folderTitle : null;
        }

        // "Show S2 - 08" leaves the season to the absolute-number adapter, which
        // has none; the name still said it.
        if (parsed.Episode is not null)
        {
            Match seasonWord = SeasonWordBeforeEpisode().Match(cleanedFileName);
            bool spelledOut =
                seasonWord.Groups["n"].Success && !TitleEpisodeMarker().IsMatch(cleanedFileName);
            if (seasonWord.Success && (parsed.Season is null or 0 or 1 || spelledOut))
            {
                string digits = seasonWord.Groups["n"].Success
                    ? seasonWord.Groups["n"].Value
                    : seasonWord.Groups["n2"].Value;
                parsed.Season = int.Parse(digits, CultureInfo.InvariantCulture);
            }
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
        int? folderSeason =
            directoryName.TryGetFolderSeason() ?? SeasonFromFolderSuffix(directoryName);

        if (parsed is { Episode: not null, Season: null })
            parsed.Season = folderSeason ?? 1;
        else if (
            parsed is { Episode: not null, Season: not null }
            && folderSeason is not null
            && parsed.Season != folderSeason
            && !SeasonWordBeforeEpisode().IsMatch(cleanedFileName)
            && !TitleEpisodeMarker().IsMatch(cleanedFileName)
            && !StringExtensions.MatchCrossFormatEpisode().IsMatch(rawFileName)
        )
            parsed.Season = folderSeason;

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

    /// <summary>"2012.AC3.720p": a number followed by release tags is a title; a bare "01" is an episode.</summary>
    private static bool NumberIsFollowedByTags(string rawFileName, Match number)
    {
        string rest = rawFileName[(number.Index + number.Length)..];
        return ReleaseTokenInTitle().IsMatch(rest)
            || TitleEpisodeMarker().IsMatch(rest)
            || BareYear().IsMatch(rest);
    }

    private readonly record struct ExplicitMarker(
        int? Season,
        int Episode,
        int Index,
        bool Strong,
        int End
    );

    /// <summary>A season/episode spelled in a form no adapter reads; the first one listed wins.</summary>
    private static void ApplyExplicitMarker(
        MovieFile parsed,
        string cleanedFileName,
        string rawFileName,
        string? directoryName
    )
    {
        ExplicitMarker? own = FindExplicitMarker(cleanedFileName) ?? FindChineseMarker(rawFileName);
        string leaf = TechnicalSize()
            .Replace(directoryName?.LeafSegment() ?? string.Empty, RemoveIfPictureSize);

        if (own is null || own.Value.Strong is false)
        {
            ExplicitMarker? folder = leaf.Length > 0 ? FindExplicitMarker(leaf) : null;
            bool fileHasNoMarker = own is null;
            if (
                folder is { } found
                && fileHasNoMarker
                && (
                    parsed.Episode is null
                    || DigitsOnlyName().IsMatch(rawFileName)
                    || IsReversedReleaseName(rawFileName)
                    || parsed.Episode == (found.Season ?? 0) * 100 + found.Episode
                    || parsed.Episode == (found.Season ?? 0) * 1000 + found.Episode
                )
            )
            {
                parsed.Season = found.Season ?? parsed.Season;
                parsed.Episode = found.Episode;
                string show = leaf[..found.Index].Replace('.', ' ').Replace('_', ' ');
                show = show.Trim(' ', '-');
                if (BareYear().Match(show) is { Success: true, Index: > 0 } yearInShow)
                    show = show[..yearInShow.Index].TrimEnd(' ', '-');
                if (show.Any(char.IsLetterOrDigit))
                    parsed.Title = show;
                return;
            }

            // Folder "Show.Name.S01.720p" and file "show.name.0106": season 1, episode 6.
            if (
                fileHasNoMarker
                && folder is null
                && parsed.Episode is int packed
                && FolderSeasonToken().Match(leaf) is { Success: true } token
                && int.Parse(token.Groups["n"].Value, CultureInfo.InvariantCulture)
                    is > 0
                        and var folderNumber
                && packed / 100 == folderNumber
            )
            {
                parsed.Season = folderNumber;
                parsed.Episode = packed % 100;
                return;
            }
        }

        if (own is not { } marker)
            return;

        int at = Math.Min(marker.End, cleanedFileName.Length);
        if (FractionRightAfter().IsMatch(cleanedFileName[at..]))
            return;

        bool absoluteBefore =
            parsed.Episode is not null
            && AbsoluteEpisodeForm().IsMatch(cleanedFileName[..Math.Min(marker.Index, at)]);
        if (absoluteBefore)
            return;

        if (marker.Strong || parsed.Episode is null)
        {
            parsed.Episode = marker.Episode;
            if (marker.Season is not null)
                parsed.Season = marker.Season;
        }
    }

    private static ExplicitMarker? FindExplicitMarker(string text)
    {
        Match match = ExplicitSeasonEpisode().Match(text);
        if (match.Success)
            return Marker(match, true);

        match = SeasonWordEpisodeWord().Match(text);
        if (match.Success)
            return Marker(match, true);

        if (FindChineseMarker(text) is { } chinese)
            return chinese;

        match = EpisodeOfTotal().Match(text);
        if (
            match.Success
            && int.Parse(match.Groups["t"].Value, CultureInfo.InvariantCulture)
                >= int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture)
        )
            return Marker(match, true);

        match = LocalizedEpisode().Match(text);
        if (match.Success)
            return Marker(match, true);

        match = CrossFormatMarker().Match(text);
        if (match.Success)
            return Marker(match, false);

        match = CapituloMarker().Match(text);
        if (match.Success)
        {
            int packed = int.Parse(match.Groups["se"].Value, CultureInfo.InvariantCulture);
            return new(packed / 100, packed % 100, match.Index, false, match.Index + match.Length);
        }

        match = LeadingSeasonDashEpisode().Match(text);
        return match.Success ? Marker(match, true) : null;
    }

    private static ExplicitMarker Marker(Match match, bool strong) =>
        new(
            match.Groups["s"].Success
                ? int.Parse(match.Groups["s"].Value, CultureInfo.InvariantCulture)
                : null,
            int.Parse(match.Groups["e"].Value, CultureInfo.InvariantCulture),
            match.Index,
            strong,
            match.Index + match.Length
        );

    /// <summary>"sbuS LN-1.5DD LD-BEW p0801": a release name written backwards to hide it.</summary>
    private static bool IsReversedReleaseName(string rawFileName) =>
        !ReleaseTokenInTitle().IsMatch(rawFileName)
        && ReleaseTokenInTitle().IsMatch(new string([.. rawFileName.Reverse()]));

    /// <summary>"Friends S03" and "庆余年第二季": a season printed at the end of the folder name.</summary>
    private static int? SeasonFromFolderSuffix(string? directoryName)
    {
        string leaf = directoryName?.LeafSegment() ?? string.Empty;
        Match suffix = FolderSeasonSuffix().Match(leaf);
        if (suffix.Success)
            return int.Parse(suffix.Groups["n"].Value, CultureInfo.InvariantCulture);

        Match chinese = ChineseSeason().Match(leaf);
        if (chinese.Success)
            return ChineseNumber(chinese.Groups["s"].Value);

        Match token = FolderSeasonToken().Match(leaf);
        return token.Success && !TitleEpisodeMarker().IsMatch(leaf)
            ? int.Parse(token.Groups["n"].Value, CultureInfo.InvariantCulture)
            : null;
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S0*(?<n>[0-9]{1,3})\s*$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FolderSeasonSuffix();

    private static ExplicitMarker? FindChineseMarker(string text)
    {
        Match match = ChineseEpisode().Match(text);
        if (!match.Success || ChineseNumber(match.Groups["e"].Value) is not int episode)
            return null;

        Match season = ChineseSeason().Match(text);
        int? seasonNumber = season.Success ? ChineseNumber(season.Groups["s"].Value) : null;
        return new(
            seasonNumber,
            episode,
            Math.Min(match.Index, season.Success ? season.Index : match.Index),
            true,
            Math.Max(match.Index + match.Length, season.Success ? season.Index + season.Length : 0)
        );
    }

    /// <summary>1 to 99 in Chinese numerals: 二, 十, 十一, 二十三.</summary>
    private static int? ChineseNumber(string text)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out int plain))
            return plain;

        const string Digits = "零一二三四五六七八九";
        int tens = text.IndexOf('十');
        if (tens < 0)
            return text.Length == 1 && Digits.IndexOf(text[0]) is var one and >= 0 ? one : null;

        int high = tens == 0 ? 1 : Digits.IndexOf(text[0]);
        int low = tens + 1 < text.Length ? Digits.IndexOf(text[tens + 1]) : 0;
        return high < 0 || low < 0 ? null : high * 10 + low;
    }

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S(?<s>[0-9]{1,4})[\s._-]*(?:E|Ep|x(?!26[45])E?)[\s._]*(?<e>[0-9]{1,5})(?![0-9])|"
            + @"(?<![A-Za-z0-9])T(?<s>[0-9]{1,2})X?E(?<e>[0-9]{1,3})(?![0-9])|"
            + @"(?<![A-Za-z0-9])E(?<e>[0-9]{1,3})[\s._-]+S(?<s>[0-9]{4})(?![0-9A-Za-z])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ExplicitSeasonEpisode();

    [GeneratedRegex(
        @"(?<![A-Za-z])(?:season|se|seizoen|saison|staffel|temporada)\.?[\s._]*(?<s>[0-9]{1,3})[\s._,()-]*"
            + @"(?:episode|epi|ep|afl|folge|episodio)\.?[\s._]*(?<e>[0-9]{1,4})(?![0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SeasonWordEpisodeWord();

    [GeneratedRegex(
        @"第(?<e>[0-9]{1,4}|[零一二三四五六七八九十]{1,3})(?:-[0-9]{1,4})?[集话話]|\[(?<e>[0-9]{1,4})[集话話完]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ChineseEpisode();

    [GeneratedRegex(
        @"第(?<s>[0-9]{1,2}|[零一二三四五六七八九十]{1,3})[季期]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ChineseSeason();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<!seasons?[\s._-]*)(?<e>[0-9]{1,3})[\s._]*(?:(?:серия|серии)[\s._]*)?"
            + @"(?:of|di|din|de|из|von|sur|van)[\s._]*(?<t>[0-9]{1,3})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex EpisodeOfTotal();

    /// <summary>Turkish "7.Bolum", "79.BLM"; Dutch and German "afl.3", "Folge 3": an episode word with no season.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<e>[0-9]{1,4})[\s._]*(?:b[oö]l[uü]m|blm)(?![A-Za-z])|"
            + @"(?<![A-Za-z])(?:b[oö]l[uü]m|blm|afl|folge)\.?[\s._]*(?<e>[0-9]{1,4})(?![0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LocalizedEpisode();

    /// <summary>"11-02 The Series Reaction": a name that opens with season-episode.</summary>
    [GeneratedRegex(
        @"^(?<s>[0-9]{1,2})-(?<e>[0-9]{2})(?=[\s._]+\p{L})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LeadingSeasonDashEpisode();

    /// <summary>"Cap.113", "Capitulo 1503": Spanish season and episode, SEE packed.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z])cap(?:itulo)?\.?[\s_]*(?<se>[0-9]{3,4})(?![0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CapituloMarker();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])S0*(?<n>[0-9]{1,2})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FolderSeasonToken();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<s>[0-9]{1,4})x(?<e>[0-9]{1,4})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CrossFormatMarker();

    [GeneratedRegex(
        @"(?<![A-Za-z])Episode[\s._-]*(?:[0-9]{1,2}|[IVX]{1,4})(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex MovieEpisodeWord();

    [GeneratedRegex(@"^[0-9][0-9\s._-]*$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DigitsOnlyName();

    [GeneratedRegex(
        @"^\.[1-9](?![0-9A-Za-z]|-[0-9])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FractionRightAfter();

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
        @"(?<![A-Za-z0-9])S[0-9]{1,4}[\s._-]*E[0-9]{1,5}(?![A-Za-z0-9])",
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
        if (
            TitleEpisodeMarker().IsMatch(group.Value)
            || SeasonWordEpisodeWord().IsMatch(group.Value)
            || (group.Index > 0 && ParenthesizedEpisodeWord().IsMatch(group.Value))
        )
            return group.Value;

        if (group.Index > 0)
        {
            Match capitulo = CapituloGroup().Match(group.Value);
            if (capitulo.Success)
            {
                string se = capitulo.Groups["se"].Value;
                return $" S{se[..^2]}E{se[^2..]} ";
            }

            Match bare = BareEpisodeGroup().Match(group.Value);
            if (bare.Success && !IsResolutionNumber(bare.Groups["n"].Value))
                return $" - {bare.Groups["n"].Value} ";
        }

        return group.Index > 0 && StringExtensions.MatchCrossFormatEpisode().IsMatch(group.Value)
            ? group.Value
            : string.Empty;
    }

    // "[Group][Show Name][01][1080p]": the show is the first bracket that is
    // neither release vocabulary nor a checksum, after the group when both exist.
    private static string? TitleFromBrackets(string rawFileName)
    {
        List<string> candidates = [];
        foreach (Match bracket in BracketGroup().Matches(rawFileName))
        {
            string inner = bracket.Groups["inner"].Value.Trim();
            if (
                !inner.Any(char.IsLetter)
                || inner.Length < 2
                || BracketChecksum().IsMatch(inner)
                || ReleaseTokenInTitle().Match(inner) is { Success: true, Index: 0 }
            )
                continue;
            candidates.Add(inner.Replace('_', ' '));
        }

        return candidates.Count switch
        {
            0 => null,
            1 => candidates[0],
            _ => candidates[1],
        };
    }

    [GeneratedRegex(@"\[(?<inner>[^\[\]]+)\]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex BracketGroup();

    [GeneratedRegex(
        @"^(?:[0-9A-Fa-f]{8}|[0-9]{3,4}x[0-9]{3,4})$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BracketChecksum();

    private static bool IsResolutionNumber(string number) =>
        number is "480" or "576" or "720" or "360" or "240";

    /// <summary>"(Ep 6)", "[Episode 12]": a group that holds nothing but an episode word and its number.</summary>
    [GeneratedRegex(
        @"^\(\s*(?:ep|episode)\.?[\s._]*[0-9]{1,4}\s*\)$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ParenthesizedEpisodeWord();

    /// <summary>"[01]", "[12END]", "[01 of 24]": a group that holds nothing but the episode.</summary>
    [GeneratedRegex(
        @"^[\[{]\s*(?<n>[0-9]{1,3})(?:v[0-9])?(?:\s*(?:end|fin))?(?:[\s_]*of[\s_]*[0-9]{1,3})?\s*[\]}]$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BareEpisodeGroup();

    /// <summary>"[Cap.102]", "[Cap.1503_1506]": Spanish season and episode, SEE packed.</summary>
    [GeneratedRegex(
        @"^\[\s*cap\.?\s*(?<se>[0-9]{3,4})(?:[_-][0-9]{3,4})?(?:[._\s][A-Za-z]+)?\s*\]$",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex CapituloGroup();

    [GeneratedRegex(
        @"^[\s.\-_]*(?<t>[0-9]{1,4})(?![0-9A-Za-z])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LeadingNumberTitle();

    /// <summary>A year in a release folder, unless it starts an air date like 2014.08.15.</summary>
    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?<year>(?:18|19|20)[0-9]{2})(?![A-Za-z0-9])(?![._-][0-9]{2}[._-][0-9]{2})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FolderBareYear();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:18|19|20)[0-9]{2}(?![A-Za-z0-9])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BareYear();

    /// <summary>"[2014]" and "[Xvid 1987]": a year the release put in square brackets.</summary>
    [GeneratedRegex(
        @"\[(?:[^\[\]]*?[\s,.-])?(?<year>(?:18|19|20)[0-9]{2})(?:[\s,.-][^\[\]]*)?\]",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BracketedYear();

    private static string? NullIfEmpty(string value) => value.Length == 0 ? null : value;

    [GeneratedRegex(
        @"(?<![0-9A-Za-z])[0-9]{3,4}x[0-9]{3,4}(?![0-9A-Za-z])|(?<![A-Za-z0-9])[xh][\s._]?26[45](?![0-9])"
            + @"|(?<![0-9A-Za-z])[0-9]+(?:\.[0-9]+)?\s?(?:mbits?|kbits?|mbps|kbps|fps)(?![A-Za-z])"
            + @"|(?<=[\s._])(?:480|576|720|1080|2160)(?=[\s._](?:BD|Blu-?Ray|DVD|WEB|HDTV|BDRip)(?![A-Za-z]))",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex TechnicalSize();

    /// <summary>"2016x231" is season 2016 episode 231; no picture is shorter than 240 lines.</summary>
    private static string RemoveIfPictureSize(Match size)
    {
        Match pair = PictureSizePair().Match(size.Value);
        return pair.Success && int.Parse(pair.Groups["h"].Value, CultureInfo.InvariantCulture) < 240
            ? size.Value
            : " ";
    }

    [GeneratedRegex(
        @"^(?<w>[0-9]{3,4})x(?<h>[0-9]{3,4})$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex PictureSizePair();

    /// <summary>" - 01b", "_-_111C": the part letter right behind a dashed episode number.</summary>
    [GeneratedRegex(
        @"(?<=[\s._]-[\s._]+)(?<n>[0-9]{1,4})[a-c](?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LetteredEpisode();

    /// <summary>" - 07.5", "_04.1_": a decimal episode number.</summary>
    [GeneratedRegex(
        @"(?:[\s._]-[\s._]+|_)[0-9]{1,3}\.[1-9](?![0-9A-Za-z.])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FractionalEpisode();

    [GeneratedRegex(
        @"[\s._]\((?<n>[0-9]{1,3})\)[\s._]*$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ParenthesizedEpisodeAtEnd();

    /// <summary>"[Ep.24]", "[24（END）]": an episode number alone in a bracket.</summary>
    [GeneratedRegex(
        @"\[\s*(?:(?:ep(?:isode)?|e)[\s._]*(?<n>[0-9]{1,4})|(?<n>(?!(?:360|480|576|720|1080|2160|(?:19|20)[0-9]{2})\])[0-9]{1,4})(?:[（(][^)）\]]*[)）])?)\s*\]",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BracketedEpisode();

    /// <summary>"第2期", "2期", "シーズン2": the season counted in periods.</summary>
    [GeneratedRegex(
        @"(?:第\s*(?<n>[0-9]{1,2})\s*期|(?<![0-9])(?<n>[0-9]{1,2})\s*期|シーズン\s*(?<n>[0-9]{1,2}))",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex JapaneseSeason();

    [GeneratedRegex(
        @"(?:^|[\s._]-[\s._]+)(?<n>[0-9]{1,3})(?=[\s._]+-[\s._]+\p{L})",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DashedEpisodeInMiddle();

    [GeneratedRegex(
        @"[\s._]-[\s._]+(?<n>[0-9]{1,4})(?![0-9A-Za-z])[\s._]*$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex DashedEpisodeAtEnd();

    /// <summary>"312v1": a re-release prints its version right behind the episode.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}.])(?<n>[0-9]{1,4})v[0-9]{1,2}(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex VersionedEpisode();

    /// <summary>"Show - 14 OAD": the number before the special word is the episode of that special.</summary>
    [GeneratedRegex(
        @"[\s._]-[\s._]+(?<n>[0-9]{1,4})[\s._]+(?:OAD|OVA|ONA)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SpecialEpisode();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:OAD|OVA|ONA)(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex UnnumberedSpecial();

    [GeneratedRegex(@"全[0-9]{1,4}集", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CompleteSeasonCount();

    /// <summary>"S07D1-3&amp;5": the discs of season 7.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])S(?<n>[0-9]{1,2})(?:D|DVD)[0-9]",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SeasonDisc();

    /// <summary>"Saison VII", "Season Two", "Saison sept": the season number written as a word.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:season|saison|staffel|temporada|stagione|seizoen|sezon)[\s._-]*"
            + @"(?<w>[IVX]{1,4}|one|two|three|four|five|six|seven|eight|nine|ten|"
            + @"un|deux|trois|quatre|cinq|sept|huit|neuf|dix)(?![\p{L}\p{N}])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SpelledSeason();

    private static readonly Dictionary<string, int> SeasonWords = new(
        StringComparer.OrdinalIgnoreCase
    )
    {
        ["one"] = 1,
        ["two"] = 2,
        ["three"] = 3,
        ["four"] = 4,
        ["five"] = 5,
        ["six"] = 6,
        ["seven"] = 7,
        ["eight"] = 8,
        ["nine"] = 9,
        ["ten"] = 10,
        ["un"] = 1,
        ["deux"] = 2,
        ["trois"] = 3,
        ["quatre"] = 4,
        ["cinq"] = 5,
        ["sept"] = 7,
        ["huit"] = 8,
        ["neuf"] = 9,
        ["dix"] = 10,
    };

    private static int? SeasonNumberFromWord(string word) =>
        SeasonWords.TryGetValue(word, out int number) ? number : RomanNumeral.TryParse(word);

    /// <summary>"[01-05]", "(01-04)", "1-24", "01..03", "01+02": no space around a dash or plus.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}-])(?<a>[0-9]{1,4})(?:-|\+|\s*\.\.\s*|\s*~\s*)(?<b>[0-9]{1,4})(?![\p{L}\p{N}-])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex BatchRange();

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:S(?<n>[0-9]{1,2})(?=(?:D|DVD)[0-9]|[\s._+&-]+S[0-9]{1,2}(?![\p{L}\p{N}]))|"
            + @"seasons?[\s._-]*(?<n>[0-9]{1,2})(?=[\s._]*(?:-|,|&|\+|to|of)[\s._]*[0-9]))",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex FirstListedSeason();

    /// <summary>"S01S02": seasons run together, so the first one is the pack's season.</summary>
    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])S(?<n>[0-9]{1,2})(?=S[0-9]{1,2}(?![\p{L}\p{N}]))",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex GluedSeasonList();

    [GeneratedRegex(
        @"(?<![0-9])(?<yy>[0-9]{2})[._-](?<mm>0[1-9]|1[0-2])[._-](?<dd>0[1-9]|[12][0-9]|3[01])(?![0-9])|"
            + @"(?<![0-9A-Za-z])(?<yy>[0-9]{2})(?<mm>0[1-9]|1[0-2])(?<dd>0[1-9]|[12][0-9]|3[01])(?![0-9A-Za-z])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ShortDate();

    /// <summary>"17.10.12" and "140722" print a date as yy.mm.dd; its year is the only year the name states.</summary>
    private static string? YearFromDate(string rawFileName)
    {
        if (TitleEpisodeMarker().IsMatch(rawFileName))
            return null;

        Match date = ShortDate().Match(rawFileName);
        if (!date.Success)
            return null;

        int twoDigits = int.Parse(date.Groups["yy"].Value, CultureInfo.InvariantCulture);
        return (twoDigits <= 40 ? 2000 + twoDigits : 1900 + twoDigits).ToString(
            CultureInfo.InvariantCulture
        );
    }

    [GeneratedRegex(
        @"\((?:[^()]*?[\s,.-])?(?<year>(?:18|19|20)[0-9]{2})(?:[\s,.-][^()]*)?\)",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ParenthesizedYear();

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
    [GeneratedRegex(
        @"[\s._]-[\s._][0-9]{1,4}(?![0-9])",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex AbsoluteEpisodeForm();

    /// <summary>A number sitting at the very end of a title, with the separator
    /// a release puts before it.</summary>
    [GeneratedRegex(
        @"[\s._-]+(?<number>[0-9]{1,4})\s*$",
        RegexOptions.None,
        matchTimeoutMilliseconds: 1000
    )]
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

    private const string OrdinalSeasonWord =
        "(?:season|сезон|temporada|staffel|évad|säsong|saison|sezon)";

    [GeneratedRegex(
        @"(?<![\p{L}\p{N}])(?:(?:S|seasons?[\s._-]*|saisons?[\s._-]*|temporada[\s._-]*|"
            + @"staffel[\s._-]*|stagione[\s._-]*|seizoen[\s._-]*|säsong[\s._-]*|сезон[\s._-]*|sezon[\s._-]*|se\.[\s._-]*)0*(?<n>[0-9]{1,2})|"
            + @"(?<n2>[0-9]{1,2})(?:st|nd|rd|th|ª|-?й|-?го)\.?[\s._-]*"
            + OrdinalSeasonWord
            + "|"
            + @"(?<n2>[0-9]{1,2})\.?[\s._-]*"
            + OrdinalSeasonWord
            + @"(?![\s._-]*[0-9]{1,2}(?![0-9A-Za-z])(?![\s._]*(?:b[oö]l[uü]m|blm|серия|серии|r[eé]sz|ep(?:isode)?(?![A-Za-z])))))"
            + @"(?:(?![\p{L}\p{N}])|(?=of[0-9]))",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex SeasonWordBeforeEpisode();

    [GeneratedRegex(
        @"(?<![A-Za-z0-9])(?:dvd-?rip|dvdr|bd-?rip|br-?rip|blu-?ray|hdtv(?:rip)?|web-?rip|web-?dl|"
            + @"xvid|divx|x26[45]|h\.?26[45]|hevc|e?ac3|dts|truefrench|vostfr|subbed|dubbed|"
            + @"repack|dirfix|nfofix|screener|(?:480|576|720|1080|2160)[pi]|3d|uncut|xxx|internal|"
            + @"multi|dl|proper|integrale|sbs|swissgerman|complete[\s._-]+(?:series|seasons?)|"
            + @"\d{1,2}(?:st|nd|rd|th)[\s._-]+seasons?|seasons?[\s._-]*\d+(?:of[0-9]+)?|saison[\s._-]*\d+|"
            + @"temporada[\s._-]*\d+|tem[\s._-]*\d+|stagione[\s._-]*\d+|staffel[\s._-]*\d+|"
            + @"amzn|dsnp|hmax|atvp|ddp?[0-9]|(?:br|bd|hd|web)mux|seizoen[\s._-]*[0-9]+|"
            + @"ep(?:isodul|isodio)?\.?[\s._-]*[0-9]+|s[0-9]{1,2}(?:d(?:vd)?[0-9]{1,2})?|[0-9]x?dvd[0-9]?|"
            + @"aka|(?:[0-9]{1,2}(?:st|nd|rd|th)[\s._-]+)?(?:special[\s._-]+)?(?:extended|collectors?|"
            + @"anniversary|ultimate|special|directors?|theatrical|unrated)[\s._-]+(?:cut|edition)|"
            + @"extras[\s._-]+only|(?<![0-9])[0-9]{2}[ ._][0-9]{2}[ ._][0-9]{2}(?![0-9])|"
            + @"camrip|hdrip|r5|boxset|vr(?:[\s._-]*180)?|[0-9](?:st|nd|rd|th)[\s._-]+cour|"
            + @"cour[\s._-]*[0-9]|limited[\s._-]+edition|第[0-9一二三四五六七八九十]+[期季話话集])"
            + @"(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex ReleaseTokenInTitle();

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
            + @"uncensored|censored|remux|batch|complete|german|french|truefrench|dutch|spanish|"
            + @"russian|internal|proper|repack|extended|edition|unrated|remastered|ts[\s._-]+line|"
            + @"(?:op|ed|ex|sp|ova|oad)[0-9]{1,2}[a-z]?|[0-9]{1,2}(?:st|nd|rd|th)|v[0-9]|ep\.?|"
            + @"nfo|dirfix|pal|ntsc|svcd|vcd|dvd[59]|cd[0-9]|ml|eps|"
            + @"s[0-9]{1,2}(?:d[0-9]{1,2})?|e[0-9]{1,3}))+\s*$"
    )]
    private static partial Regex TrailingReleaseNoise();

    /// <summary>A tracker address whose dots became spaces: "www torrenting com Show".</summary>
    [GeneratedRegex(
        @"^www[\s._]+[A-Za-z0-9\s._-]*?[\s._]+(?:com|net|org|world|to|me|cc|tv|info|io|ws|vip|xyz|pw|site|club|live)"
            + @"(?![A-Za-z0-9])[\s._-]*",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
    private static partial Regex LeadingTrackerWords();

    [GeneratedRegex(
        matchTimeoutMilliseconds: 1000,
        options: RegexOptions.IgnoreCase,
        pattern: @"(?:[\s._+-]+(?:(?:part|vol(?:ume)?|dis[ck]|episode|episodio|capitulo|ep|seasons?|"
            + @"second[\s._-]+season|complete[\s._-]+version|complete[\s._-]+collection)\.?[\s._+-]*"
            + @"(?:[0-9]{1,3}|one|two|three|four|five|six|seven|eight|nine|ten)?"
            + @"(?:[\s._+-]*(?:of|de|di|din)(?:[\s._+-]*[0-9]{1,3})?)?|series[\s._-]*[0-9]{1,2}))+\s*$"
    )]
    private static partial Regex TvPackNoise();

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
    [GeneratedRegex(
        @"(?<=[0-9])v[0-9]{1,2}(?![A-Za-z0-9])",
        RegexOptions.IgnoreCase,
        matchTimeoutMilliseconds: 1000
    )]
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
