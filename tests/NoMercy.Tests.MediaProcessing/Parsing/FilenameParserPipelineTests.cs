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

using MovieFileLibrary;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Files.Parsing.Adapters;
using NoMercy.NmSystem.Extensions;
using NoMercy.Tests.Common.TestMedia;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Corpus-driven tests for the filename parse pipeline. Every adapter is backed by
/// many real scene/western/anime/movie names, and the ordering tests prove no
/// adapter under-classifies (claims a name a more-specific adapter should own).
/// </summary>
public class FilenameParserPipelineTests
{
    private static IFilenameParseAdapter[] DefaultAdapters() =>
        new IFilenameParseAdapter[]
        {
            new EpisodePrefixAdapter(),
            new EpisodeWordAdapter(),
            new CrossFormatAdapter(),
            new SeasonEpisodeAdapter(),
            new SeasonSpecialAdapter(),
            new AnimeAbsoluteAdapter(),
            new EpisodeShortFormAdapter(),
            new SpecialsAdapter(),
            new MovieDetectorAdapter(),
        };

    private static IFilenameParserPipeline Pipeline() =>
        new FilenameParserPipeline(DefaultAdapters());

    private static ParseContext Context(
        string fileName,
        string folderTitle = "",
        string libraryType = "tv"
    )
    {
        string cleaned = StringExtensions
            .RemoveBracketedString()
            .Replace(Path.GetFileNameWithoutExtension(fileName), string.Empty)
            .Trim();

        return new()
        {
            FileNameWithExtension = fileName,
            DirectoryName = null,
            Title = StringExtensions.RemoveBracketedString().Replace(fileName, string.Empty),
            CleanedFileName = cleaned,
            FolderTitle = folderTitle,
            LibraryType = libraryType,
        };
    }

    /// <summary>The first adapter (in effective order) that returns a result.</summary>
    private static string? FirstMatchingAdapter(ParseContext context)
    {
        foreach (IFilenameParseAdapter adapter in DefaultAdapters().OrderBy(a => a.Order))
            if (adapter.TryParse(context) is not null)
                return adapter.Name;
        return null;
    }

    // ---------------------------------------------------------------------------
    // SxxExx anywhere (the bread-and-butter scene format)
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["One.Piece.S01E1109.1080p.WEB-DL.mkv", 1, 1109])]
    [InlineData(["The.Office.US.S03E12.720p.HDTV.x264-LOL.mkv", 3, 12])]
    [InlineData(["Breaking.Bad.S05E14.1080p.BluRay.x265-GROUP.mkv", 5, 14])]
    [InlineData(["Game of Thrones - S08E06 - The Iron Throne.mkv", 8, 6])]
    [InlineData(["Mr_Robot_S02E05_720p.mkv", 2, 5])]
    [InlineData(["Stranger.Things.S04E09.2160p.NF.WEB-DL.DDP5.1.x265-NTb.mkv", 4, 9])]
    [InlineData(["Better.Call.Saul.S06E13.PROPER.1080p.mkv", 6, 13])]
    [InlineData(["Rick and Morty S05E10 1080p.mkv", 5, 10])]
    [InlineData(["Dark.S01E01.German.DL.1080p.BluRay.x264-GROUP.mkv", 1, 1])]
    [InlineData(["The.Mandalorian.S02E08.2160p.WEB-DL.mkv", 2, 8])]
    [InlineData(["Severance.S01E09.1080p.ATVP.WEBRip.mkv", 1, 9])]
    [InlineData(["Show.Name.s1e3.mkv", 1, 3])]
    public void SeasonEpisode_anywhere_resolves(string file, int season, int episode)
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.IsSeries.Should().BeTrue();
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(Context(file)).Should().Be("season-episode");
    }

    // ---------------------------------------------------------------------------
    // SxxExx at the very start (title comes from the folder)
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["S01E05 - The Title.mkv", 1, 5])]
    [InlineData(["S03E10.1080p.mkv", 3, 10])]
    [InlineData(["s1e1.mkv", 1, 1])]
    [InlineData(["S12E24-Finale.mkv", 12, 24])]
    public void EpisodePrefix_at_start_uses_folder_title(string file, int season, int episode)
    {
        const string folder = "The Show Name";
        MovieFile result = Pipeline().Parse(Context(file, folder));
        result.IsSeries.Should().BeTrue();
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        result.Title.Should().Be(folder);
        FirstMatchingAdapter(Context(file, folder)).Should().Be("episode-prefix");
    }

    // ---------------------------------------------------------------------------
    // "Episode NN" word form (separator required before the word)
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["Blade - Episode 02 - title.mp4", 2])]
    [InlineData(["Naruto Episode 130.mkv", 130])]
    [InlineData(["Cowboy Bebop - Episode 5.mkv", 5])]
    [InlineData(["Some.Show.Episode12.mkv", 12])]
    public void EpisodeWord_resolves(string file, int episode)
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.IsSeries.Should().BeTrue();
        result.Season.Should().Be(1);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(Context(file)).Should().Be("episode-word");
    }

    // ---------------------------------------------------------------------------
    // Movies must NOT be under-classified as episodes by any series adapter.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Inception.2010.1080p.BluRay.x264-GROUP.mkv")]
    [InlineData("The.Matrix.1999.REMASTERED.1080p.BluRay.x265.mkv")]
    [InlineData("Dune.Part.Two.2024.2160p.WEB-DL.DDP5.1.x265-FLUX.mkv")]
    [InlineData("Avengers.Endgame.2019.UHD.BluRay.2160p.mkv")]
    [InlineData("Blade Runner 2049 (2017).mkv")]
    [InlineData("Top.Gun.Maverick.2022.IMAX.1080p.mkv")]
    public void Movies_are_not_classified_as_episodes(string file)
    {
        ParseContext context = Context(file, libraryType: "movie");
        new EpisodePrefixAdapter().TryParse(context).Should().BeNull();
        new EpisodeWordAdapter().TryParse(context).Should().BeNull();
        new SeasonEpisodeAdapter().TryParse(context).Should().BeNull();
        new CrossFormatAdapter().TryParse(context).Should().BeNull();
        new AnimeAbsoluteAdapter().TryParse(context).Should().BeNull();
        FirstMatchingAdapter(context).Should().Be("movie-detector");
    }

    // ---------------------------------------------------------------------------
    // Ordering / no under-classification: the FIRST adapter to match must be the
    // most specific correct one.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["One.Piece.S01E1109.mkv", "season-episode"])]
    [InlineData(["Blade - Episode 02.mkv", "episode-word"])]
    [InlineData(["Inception.2010.1080p.mkv", "movie-detector"])]
    public void Pipeline_first_match_is_expected_adapter(string file, string expected) =>
        FirstMatchingAdapter(Context(file, "Folder Title")).Should().Be(expected);

    [Fact]
    public void EpisodePrefix_at_start_wins_over_season_episode_for_same_name()
    {
        // "S01E05..." matches the start-anchored prefix; the .SxxExx (separator
        // required) form deliberately does NOT match a leading token, so prefix owns it.
        ParseContext context = Context("S01E05.mkv", "My Show");
        new EpisodePrefixAdapter().TryParse(context).Should().NotBeNull();
        new SeasonEpisodeAdapter().TryParse(context).Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Raw regex contracts (positive + negative) so each pattern is pinned down.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["S01E01", true])]
    [InlineData(["s1e1", true])]
    [InlineData(["S01E1109", true])]
    [InlineData(["S12E100", true])]
    [InlineData(["Show.S01E01", false])]
    [InlineData(["Movie.2010.1080p", false])]
    [InlineData(["1x01", false])]
    public void MatchEpisodePrefix_contract(string input, bool expected) =>
        StringExtensions.MatchEpisodePrefix().IsMatch(input).Should().Be(expected);

    [Theory]
    [InlineData(["Show.S01E01", true])]
    [InlineData(["Show S01E01", true])]
    [InlineData(["Show-S01E01", true])]
    [InlineData(["Show_S02E05", true])]
    [InlineData(["Show - 29 (S02E01)", true])]
    [InlineData(["Show [S01E05] 1080p", true])]
    [InlineData(["S01E01", false])]
    [InlineData(["ShowS01E01", false])]
    [InlineData(["Show.2010.1080p", false])]
    public void MatchSeasonEpisode_contract(string input, bool expected) =>
        StringExtensions.MatchSeasonEpisode().IsMatch(input).Should().Be(expected);

    [Theory]
    [InlineData([" Episode 5", true])]
    [InlineData(["- Episode 02", true])]
    [InlineData([".Episode12", true])]
    [InlineData(["Show Episode 130", true])]
    [InlineData(["Episode 5", false])]
    [InlineData(["Show.Episode.5", false])]
    public void MatchEpisodeWord_contract(string input, bool expected) =>
        StringExtensions.MatchEpisodeWord().IsMatch(input).Should().Be(expected);

    [Theory]
    [InlineData(["Movie 2010", true])]
    [InlineData(["Show 1999", true])]
    [InlineData(["Film 2024", true])]
    [InlineData(["Video 1080p", false])]
    [InlineData(["Clip 2160p", false])]
    [InlineData(["Thing 12345", false])]
    public void MatchYearRegex_contract(string input, bool expected) =>
        StringExtensions.MatchYearRegex().IsMatch(input).Should().Be(expected);

    // ---------------------------------------------------------------------------
    // Pipeline configurability: disable + reorder via FilenameParsingOptions.
    // ---------------------------------------------------------------------------
    [Fact]
    public void Disabling_an_adapter_removes_it_from_the_pipeline()
    {
        FilenameParsingOptions options = new() { Disabled = { "season-episode" } };
        FilenameParserPipeline pipeline = new(DefaultAdapters(), options);
        pipeline.Order.Should().NotContain("season-episode");
        pipeline.Order.Should().Contain("movie-detector");
    }

    [Fact]
    public void Order_override_runs_named_adapter_first()
    {
        FilenameParsingOptions options = new() { Order = { "movie-detector" } };
        FilenameParserPipeline pipeline = new(DefaultAdapters(), options);
        pipeline.Order.First().Should().Be("movie-detector");
    }

    // ---------------------------------------------------------------------------
    // Cross-format "1x05" (and unicode "1×05"). Resolution like 1920x1080 must
    // NOT match (digits before the separator are preceded by a digit).
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["Battlestar.Galactica.3x20.1080p.mkv", 3, 20])]
    [InlineData(["Show Name 1x05.mkv", 1, 5])]
    [InlineData(["Series.12x08.720p.mkv", 12, 8])]
    [InlineData(["Farscape - 1×01 - Pilot.mkv", 1, 1])]
    public void CrossFormat_resolves(string file, int season, int episode)
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.IsSeries.Should().BeTrue();
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(Context(file)).Should().Be("cross-format");
    }

    [Theory]
    [InlineData("Show.Name.1920x1080.SBS.mkv")]
    [InlineData("Clip.1280x720.mkv")]
    public void CrossFormat_ignores_resolution(string file) =>
        new CrossFormatAdapter().TryParse(Context(file)).Should().BeNull();

    // ---------------------------------------------------------------------------
    // Anime absolute numbering (anime/TV libraries only).
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["One Piece - 1109.mkv", 1109])]
    [InlineData(["Naruto Shippuuden - 500.mkv", 500])]
    [InlineData(["Bleach - 366 [1080p].mkv", 366])]
    [InlineData(["Fairy Tail 175.mkv", 175])]
    public void AnimeAbsolute_resolves_for_anime_library(string file, int absolute)
    {
        ParseContext context = Context(file, libraryType: "anime");
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Episode.Should().Be(absolute);
        FirstMatchingAdapter(context).Should().Be("anime-absolute");
    }

    [Theory]
    [InlineData(["Firefly - 2002.mkv", "anime"])]
    [InlineData(["One Piece - 1109.mkv", "movie"])]
    [InlineData(["Inception 2010.mkv", "tv"])]
    public void AnimeAbsolute_does_not_fire(string file, string library) =>
        new AnimeAbsoluteAdapter().TryParse(Context(file, libraryType: library)).Should().BeNull();

    [Fact]
    public void Explicit_SxxExx_wins_over_anime_absolute_in_anime_library()
    {
        ParseContext context = Context("Attack on Titan S04E28 1080p.mkv", libraryType: "anime");
        FirstMatchingAdapter(context).Should().Be("season-episode");
        MovieFile result = Pipeline().Parse(context);
        result.Season.Should().Be(4);
        result.Episode.Should().Be(28);
    }

    // ---------------------------------------------------------------------------
    // Scene-tag title cleanup: derived titles are stripped of quality/source/codec
    // noise, while real titles that resemble tag substrings survive intact.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["Some Show 1080p WEB-DL - Episode 05.mkv", "Some Show", 5])]
    [InlineData(["Another.Show.720p.HDTV - Episode 12.mp4", "Another Show", 12])]
    public void Title_cleanup_episode_word(string file, string title, int episode)
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(Context(file)).Should().Be("episode-word");
    }

    [Theory]
    [InlineData(["Some Show 1080p - 12.mkv", "Some Show", 12])]
    public void Title_cleanup_anime_absolute(string file, string title, int episode)
    {
        ParseContext context = Context(file, libraryType: "anime");
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be("anime-absolute");
    }

    [Theory]
    [InlineData(["Reacher.S01E01.1080p.WEB-DL.mkv", "Reacher", 1, 1])]
    [InlineData(["Limitless.S01E05.720p.HDTV.x264.mkv", "Limitless", 1, 5])]
    [InlineData(["Breaking.Bad.S05E14.1080p.BluRay.x265-GROUP.mkv", "Breaking Bad", 5, 14])]
    public void Title_cleanup_preserves_real_titles_in_season_episode(
        string file,
        string title,
        int season,
        int episode
    )
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(Context(file)).Should().Be("season-episode");
    }

    // ---------------------------------------------------------------------------
    // Short episode forms (E05 / Ep5 / S01.E05) that omit the contiguous SxxExx.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["Naruto.E12.1080p.WEB-DL.mkv", "Naruto", 1, 12])]
    [InlineData(["Show Name Ep5.mkv", "Show Name", 1, 5])]
    [InlineData(["Show.Name.Ep.05.720p.mkv", "Show Name", 1, 5])]
    [InlineData(["Show.Name.S02.E07.mkv", "Show Name", 2, 7])]
    public void Episode_short_form(string file, string title, int season, int episode)
    {
        ParseContext context = Context(file);
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be("episode-short-form");
    }

    [Theory]
    [InlineData("Resident Evil 2.mkv")]
    [InlineData("iPhone5.review.mkv")]
    [InlineData("Se7en.1995.mkv")]
    [InlineData("Route.66.1080p.mkv")]
    public void Episode_short_form_does_not_over_classify(string file) =>
        new EpisodeShortFormAdapter().TryParse(Context(file)).Should().BeNull();

    [Fact]
    public void Episode_short_form_is_skipped_for_movie_libraries() =>
        new EpisodeShortFormAdapter()
            .TryParse(Context("Show.E05.mkv", libraryType: "movie"))
            .Should()
            .BeNull();

    // ---------------------------------------------------------------------------
    // Specials / season-zero: labelled (OVA/SP/NCED/Special) rather than numbered.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["One Piece - OVA 3.mkv", "anime", "One Piece", 3])]
    [InlineData(["Bleach.SP01.mkv", "tv", "Bleach", 1])]
    [InlineData(["Show.Name.NCED.mkv", "tv", "Show Name", 1])]
    [InlineData(["My Show - Special 2.mkv", "tv", "My Show", 2])]
    [InlineData(["My Show - Specials.mkv", "tv", "My Show", 1])]
    public void Specials_season_zero(string file, string lib, string title, int episode)
    {
        ParseContext context = Context(file, libraryType: lib);
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be("specials");
    }

    [Fact]
    public void Specials_uses_folder_for_bare_anime_marker()
    {
        ParseContext context = Context("OVA 2.mkv", "One Piece", "anime");
        MovieFile result = Pipeline().Parse(context);
        result.Title.Should().Be("One Piece");
        result.Season.Should().Be(0);
        result.Episode.Should().Be(2);
        FirstMatchingAdapter(context).Should().Be("specials");
    }

    // ---------------------------------------------------------------------------
    // Season-scoped specials: "SxxSyy" — season xx's special yy. Judas, Erai-raws
    // and most BD batch releases number extras this way, and nothing recognised
    // it: the name fell through every adapter to the movie detector, which
    // returned no season and no episode, and the caller's last-resort "first
    // number in the title" rule then read the SEASON digits as the episode. Every
    // special in a season therefore arrived as SxxE01 — eight files claiming one
    // episode, each overwriting the last.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["[Judas] Overlord - S01S01.mkv", "Overlord", 1])]
    [InlineData(["[Judas] Overlord - S01S08.mkv", "Overlord", 8])]
    [InlineData(["[Judas] Overlord - S02S13.mkv", "Overlord", 13])]
    [InlineData(["[Judas] Overlord - S03S02.mkv", "Overlord", 2])]
    public void Season_scoped_specials_are_season_zero(string file, string title, int episode)
    {
        ParseContext context = Context(file, libraryType: "anime");
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be("season-special");
    }

    [Fact]
    public void Season_scoped_specials_do_not_collide_on_one_episode()
    {
        int[] episodes =
        [
            .. new[] { 1, 2, 3 }.Select(n =>
                Pipeline()
                    .Parse(Context($"[Judas] Overlord - S01S0{n}.mkv", libraryType: "anime"))
                    .Episode
                ?? 0
            ),
        ];

        episodes.Should().Equal(1, 2, 3);
    }

    [Theory]
    // A real episode marker keeps its own meaning, and a show whose name merely
    // contains the letter S next to digits is not a special.
    [InlineData("[Judas] Overlord - S01E01.mkv")]
    [InlineData("The.Office.US.S03E12.720p.mkv")]
    [InlineData("Bleach.S00E05.1080p.mkv")]
    public void Season_scoped_specials_do_not_over_classify(string file) =>
        new SeasonSpecialAdapter().TryParse(Context(file, libraryType: "anime")).Should().BeNull();

    [Fact]
    public void A_half_episode_keeps_the_season_it_was_numbered_in()
    {
        SeasonSpecialAdapter adapter = new();
        MovieFile? first = adapter.TryParse(Context("NANA - S01E21.5.mkv", libraryType: "anime"));
        MovieFile? second = adapter.TryParse(Context("NANA - S02E21.5.mkv", libraryType: "anime"));

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Season.Should().Be(1, "S01E21.5 is a half episode of season 1");
        second!.Season.Should().Be(2, "S02E21.5 is a half episode of season 2");
        first.Episode.Should().BeNull("no whole episode is numbered 21.5");
        first.IsSpecialEpisode.Should().BeTrue();
    }

    [Fact]
    public void A_season_scoped_special_still_maps_to_season_zero()
    {
        MovieFile? special = new SeasonSpecialAdapter().TryParse(
            Context("[Judas] Overlord - S01S21.mkv", libraryType: "anime")
        );

        special.Should().NotBeNull();
        special!.Season.Should().Be(0);
        special.Episode.Should().Be(21);
    }

    [Fact]
    public void Season_scoped_specials_skipped_for_movie_libraries() =>
        new SeasonSpecialAdapter()
            .TryParse(Context("[Judas] Overlord - S01S01.mkv", libraryType: "movie"))
            .Should()
            .BeNull();

    [Theory]
    // The season tag sits between the show name and the marker in a BD batch
    // ("Overlord - S01 NCOP"), and the title was taken as everything before the
    // marker — so the show went out to the providers as "Overlord - S01", which
    // matches nothing.
    [InlineData("[Judas] Overlord - S01 NCOP.mkv")]
    [InlineData("[Judas] Overlord - S02 NCED.mkv")]
    public void Labelled_specials_drop_the_season_tag_from_the_title(string file)
    {
        MovieFile result = Pipeline().Parse(Context(file, libraryType: "anime"));
        result.Title.Should().Be("Overlord");
        result.Season.Should().Be(0);
    }

    [Theory]
    // A series' films are season-zero content. They used to reach the
    // absolute-number matcher, which read the film index as an absolute episode
    // and landed both compilation films on top of the season's real episodes 1
    // and 2.
    [InlineData(["[Judas] Overlord - Movie 1.mkv", "Overlord", 1])]
    [InlineData(["[Judas] Overlord - Movie 2.mkv", "Overlord", 2])]
    public void Series_films_are_season_zero(string file, string title, int episode)
    {
        ParseContext context = Context(file, libraryType: "anime");
        MovieFile result = Pipeline().Parse(context);
        result.Title.Should().Be(title);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be("specials");
    }

    [Theory]
    // word markers never steal a real title, substrings never match
    [InlineData("Special.mkv")]
    [InlineData("Movie.mkv")]
    [InlineData("Extras.mkv")]
    [InlineData("Spectre.2015.1080p.mkv")]
    [InlineData("Casanova.1981.mkv")]
    [InlineData("Extraction.2020.1080p.mkv")]
    [InlineData("Nova.2021.mkv")]
    public void Specials_does_not_over_classify(string file) =>
        new SpecialsAdapter().TryParse(Context(file)).Should().BeNull();

    [Fact]
    public void Specials_skipped_for_movie_libraries() =>
        new SpecialsAdapter()
            .TryParse(Context("Show - OVA 1.mkv", libraryType: "movie"))
            .Should()
            .BeNull();

    [Fact]
    public void S00Exx_still_owned_by_season_episode()
    {
        ParseContext context = Context("Bleach.S00E05.1080p.mkv");
        MovieFile result = Pipeline().Parse(context);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(5);
        FirstMatchingAdapter(context).Should().Be("season-episode");
    }

    // ---------------------------------------------------------------------------
    // Real scene release names (scenerules.org spec; cf. pr0pz/scene-release-parser,
    // MIT). These lock title/season/episode extraction against names seen in the
    // wild, including year-in-title and split season.episode forms.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["Halo.2022.S01E06.POLISH.720p.WEB.H264-A4O.mkv", "Halo", 1, 6, "season-episode"])]
    [InlineData([
        "Gilmore.Girls.S05E01.720p.WEB-DL.AAC2.0.H.264-tK.mkv",
        "Gilmore Girls",
        5,
        1,
        "season-episode",
    ])]
    [InlineData([
        "Direct.Talk.S09E09.Mizutani.Yoshihiro.Relief.1080p.HDTV.H264-DARKFLiX.mkv",
        "Direct Talk",
        9,
        9,
        "season-episode",
    ])]
    [InlineData([
        "Dark.Net.S01E06.DOC.SUBFRENCH.720p.WEBRip.x264-TiMELiNE.mkv",
        "Dark Net",
        1,
        6,
        "season-episode",
    ])]
    [InlineData([
        "New.Amsterdam.2018.S02E12.1080p.AMZN.Webrip.x265.10bit.EAC3.5.1.mkv",
        "New Amsterdam",
        2,
        12,
        "season-episode",
    ])]
    [InlineData([
        "Stranger.Things.S04E09.2160p.NF.WEB-DL.DDP5.1.x265-NTb.mkv",
        "Stranger Things",
        4,
        9,
        "season-episode",
    ])]
    [InlineData([
        "The.X-Files.2x14.Die.Hand.Die.Verletzt.DVDRip.XviD.MultiDub-VeLVeT.mkv",
        "The X-Files",
        2,
        14,
        "cross-format",
    ])]
    [InlineData([
        "New.Amsterdam.2018.2x12.1080p.WEBMux.x264-NovaRi.mkv",
        "New Amsterdam",
        2,
        12,
        "cross-format",
    ])]
    [InlineData([
        "24.Twenty.Four.S2.E07.German.DVDRiP.Line.Dubbed.SVCD-SOF.mkv",
        "24 Twenty Four",
        2,
        7,
        "episode-short-form",
    ])]
    [InlineData(["1883.S01E04.1080p.WEB.H264.mkv", "1883", 1, 4, "season-episode"])]
    [InlineData(["1923.S01E02.720p.HDTV.x264.mkv", "1923", 1, 2, "season-episode"])]
    public void SceneCorpus_real_names(
        string file,
        string title,
        int season,
        int episode,
        string adapter
    )
    {
        ParseContext context = Context(file);
        MovieFile result = Pipeline().Parse(context);
        result.IsSeries.Should().BeTrue();
        result.Title.Should().Be(title);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
        FirstMatchingAdapter(context).Should().Be(adapter);
    }

    [Theory]
    // a cross-format token that IS the title must not be read as SxExx
    [InlineData("4x4.2019.1080p.BluRay.x264-GRP.mkv")]
    [InlineData("4x4.mkv")]
    public void CrossFormat_ignores_leading_token(string file) =>
        new CrossFormatAdapter().TryParse(Context(file)).Should().BeNull();

    // ---------------------------------------------------------------------------
    // Hard real-world corpus (harvested from the MIT pr0pz/scene-release-parser
    // fixtures). These assert the *parse outcome* of long, messy, real scene names
    // — not the tokens a regex was built from — so they prove behaviour rather than
    // restating vocabulary. Several are adversarial (a title that opens with "4x4",
    // stray "9.00" numbers, a year before the SxxExx, an "x" inside the title).
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData([
        "24.S02E02.9.00.Uhr.bis.10.00.Uhr.German.DL.TV.Dubbed.DVDRip.SVCD.READ.NFO-c0nFuSed",
        2,
        2,
    ])]
    [InlineData(["4x4.Ule.ja.Umber.Autoga.Colombias.S01E09.EE.1080p.WEB.h264-EMX", 1, 9])]
    [InlineData(["72.Cutest.Animals.S01E0.German.DL.Doku.1080p.WEB.x264-BiGiNT", 1, 0])]
    [InlineData(["Dark.Net.S01E06.DOC.SUBFRENCH.720p.WEBRip.x264-TiMELiNE", 1, 6])]
    [InlineData([
        "Direct.Talk.S09E09.Mizutani.Yoshihiro.Relief.Beds.Made.of.Cardboard.1080p.HDTV.H264-DARKFLiX",
        9,
        9,
    ])]
    [InlineData(["Gilmore.Girls.S05E01.720p.WEB-DL.AAC2.0.H.264-tK", 5, 1])]
    [InlineData(["Halo.2022.S01E06.POLISH.720p.WEB.H264-A4O", 1, 6])]
    [InlineData(["New.Amsterdam.2018.S02E12.14.Years.mkv", 2, 12])]
    [InlineData([
        "New.Amsterdam.2018.2x12.14.Anni.2.Mesi.8.Giorni.ITA-ENG.1080p.WEBMux.x264-NovaRi",
        2,
        12,
    ])]
    public void Pipeline_ExtractsSeasonEpisode_FromHardRealNames(
        string file,
        int season,
        int episode
    )
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
    }

    [Theory]
    [InlineData([
        "Spy.x.Family.E04.Elterngespraech.an.der.Eliteschule.German.2022.ANiME.DL.BDRiP.x264-STARS",
        4,
    ])]
    public void Pipeline_ExtractsEpisode_WhenNoSeason(string file, int episode)
    {
        MovieFile result = Pipeline().Parse(Context(file));
        result.Episode.Should().Be(episode);
    }

    [Theory]
    [InlineData(["Gegen.den.Strom.2018.German.AC3D.DL.1080p.BluRay.x264-SAVASTANOS", "2018"])]
    [InlineData([
        "Batman.v.Superman.Dawn.of.Justice.2016.IMAX.German.DL.TrueHD.Atmos.DUBBED.2160p.UHD.BluRay.x265-GSG9",
        "2016",
    ])]
    [InlineData([
        "Cloudy.With.A.Chance.Of.Meatballs.2009.NORDIC.DTS-HD.DTS.AC3.NORDICSUBS.1080p.BluRay.x264-TUSAHD",
        "2009",
    ])]
    [InlineData(["Pay.the.Ghost.2015.1080p.HULU.WEB-DL.DDP.5.1.H.264-PiRaTeS", "2015"])]
    [InlineData([
        "Angel.Heart.1987.German.DTSMAD.5.1.DL.2160p.UHD.BluRay.HDR.DV.HEVC.Remux-HDSource",
        "1987",
    ])]
    [InlineData([
        "Burial.Ground.The.Nights.Of.Terror.1981.DUBBED.GRINDHOUSE.VERSION.1080P.BLURAY.X264-WATCHABLE",
        "1981",
    ])]
    [InlineData(["Intruders.Die.Aliens.Sind.Unter.Uns.1992.Uncut.German.AC3.DVDRiP.XviD", "1992"])]
    [InlineData(["V.H.S.94.2021.BluRay.1080p.DTS-HD.MA.5.1.AVC-GROUPNAME", "2021"])]
    public void Pipeline_ClassifiesMovies_NotSeries(string file, string year)
    {
        MovieFile result = Pipeline().Parse(Context(file, "", "movies"));
        result.IsSeries.Should().BeFalse();
        result.Year.Should().Be(year);
    }

    // ---------------------------------------------------------------------------
    // Shared test-media corpus — the same real-world filename patterns the encoder
    // input→output tests synthesise. Parsing them here proves the corpus names are
    // classified correctly (movie vs series, season/episode) before any byte is
    // encoded, so a corpus entry and its parse expectation can never drift apart.
    // ---------------------------------------------------------------------------
    public static TheoryData<string> CorpusFiles()
    {
        TheoryData<string> data = [];
        foreach (MediaCorpusEntry entry in MediaCorpus.Entries)
            data.Add(entry.RelativePath);
        return data;
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void Corpus_filenames_parse_to_expected_classification(string relativePath)
    {
        MediaCorpusEntry entry = MediaCorpus.Entries.Single(e => e.RelativePath == relativePath);

        string fileName = Path.GetFileName(entry.RelativePath);
        string folderTitle =
            Path.GetFileName(Path.GetDirectoryName(entry.RelativePath) ?? string.Empty)
            ?? string.Empty;
        string libraryType = entry.ExpectedIsMovie ? "movies" : "tv";

        MovieFile result = Pipeline().Parse(Context(fileName, folderTitle, libraryType));

        result.IsSeries.Should().Be(!entry.ExpectedIsMovie);
        if (entry.ExpectedSeason is int season)
            result.Season.Should().Be(season);
        if (entry.ExpectedEpisode is int episode)
            result.Episode.Should().Be(episode);
    }
}
