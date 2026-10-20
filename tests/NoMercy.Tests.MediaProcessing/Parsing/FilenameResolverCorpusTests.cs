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

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// What the file list actually produces for a name — the adapters plus the
/// folder-season and trailing-number rules that run around them.
/// <para>
/// Every name here is a real one, taken from a sweep of the 11,539 video paths
/// in the encode-campaign ledger — an inventory of source files, most of which
/// are not in a library. It is a corpus of real-world naming, not a statement
/// about the library's contents, and only the NAMES were run: no file was read
/// and no provider was asked.
/// </para>
/// <para>
/// Each group is a class of name that used to resolve to the wrong
/// place, and each group is followed by the names that must NOT move: these
/// rules read numbers out of release names, and a rule that is one character too
/// greedy quietly files a real episode as a special.
/// </para>
/// </summary>
public class FilenameResolverCorpusTests
{
    private static FilenameResolver Resolver() =>
        new(
            new FilenameParserPipeline(
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
                    new SeasonPackAdapter(),
                    new PartAdapter(),
                    new MovieDetectorAdapter(),
                }
            )
        );

    private static MovieFile Resolve(
        string fileName,
        string directory = @"C:\Anime\Show",
        string libraryType = "anime"
    ) =>
        Resolver()
            .Resolve(fileName, directory, Path.Combine(directory, fileName), libraryType)
            .Parsed;

    // ---------------------------------------------------------------------------
    // A re-release marks its version on the episode number: "- 01v2". The suffix
    // glues to the digits, so every matcher wanting a standalone number rejected
    // both halves and all 24 episodes of the show resolved to nothing at all.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["[Sokudo] Dororo (2019) - 01v2 [1080p BD][AV1][dual audio].mkv", 1])]
    [InlineData(["[Sokudo] Dororo (2019) - 24v2 [1080p BD][AV1][dual audio].mkv", 24])]
    [InlineData(["Detective Conan - 678v2.mkv", 678])]
    public void A_version_suffix_does_not_hide_the_episode(string file, int episode)
    {
        MovieFile result = Resolve(file);
        result.Episode.Should().Be(episode);
    }

    [Fact]
    public void A_version_in_the_show_name_is_not_a_version_suffix()
    {
        // "Ver1.1a" is part of the title. Only a v bound to a preceding digit is
        // a release version, so the whole version string survives (dots become
        // spaces in every title, which is not this rule's doing).
        MovieFile result = Resolve("[Judas] NieR-Automata Ver1.1a - S01E18.mkv");
        result.Title.Should().Contain("Ver1 1a");
        result.Season.Should().Be(1);
        result.Episode.Should().Be(18);
    }

    // ---------------------------------------------------------------------------
    // Season-scoped specials carry a marker between the season and the number.
    // Each of these used to lose the marker and land on a REAL episode of season
    // one: six One Punch Man OVAs all became S01E01.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["[Judas] One Punch Man - S01OVA05.mkv", 5])]
    [InlineData(["[Judas] Clannad - S02OVA02.mkv", 2])]
    [InlineData(["[Judas] Boku no Hero Academia - S07SP03v2.mkv", 3])]
    [InlineData(["[Judas] CHIHAYAFURU - S03SP01.mkv", 1])]
    [InlineData(["[Judas] Overlord - S02S13.mkv", 13])]
    public void A_season_scoped_special_is_season_zero(string file, int episode)
    {
        MovieFile result = Resolve(file);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
    }

    // ---------------------------------------------------------------------------
    // A recap that airs between two episodes is numbered with a half, and was
    // read as the whole episode either side of it.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["NANA - S01E21.5 (1080p VRV Dual Audio WEB-DL -KS-).mkv", 21])]
    [InlineData(["S02E05.5 [SP]-Guidepost [4227F7A0].mkv", 5])]
    [InlineData(["[Judas] NieR-Automata Ver1.1a - S01E18.5.mkv", 18])]
    public void A_half_episode_is_season_zero(string file, int episode)
    {
        MovieFile result = Resolve(file);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
    }

    [Theory]
    // The guard on the rule above. A scene name separates with dots, so both a
    // resolution tag and an episode whose TITLE starts with a number sit exactly
    // where a half-episode would. "The Punisher - 3 A.M." is episode 1, not 1.5.
    [InlineData([
        "The.Punisher.S01E01.3.AM.2160p.DSNP.WEB-DL.DDP5.1.Atmos.HDR.H.265-SMURF.mkv",
        1,
        1,
    ])]
    [InlineData(["Breaking.Bad.S05E14.1080p.BluRay.x265-GROUP.mkv", 5, 14])]
    [InlineData(["NANA - S01E21 (1080p VRV Dual Audio WEB-DL -KS-).mkv", 1, 21])]
    [InlineData(["Moon.Knight.S01E06.Episode.6.2160p.WEB-DL.DDP5.1.Atmos.DV.MKV.x265.mkv", 1, 6])]
    public void A_dot_in_a_release_name_is_not_a_half_episode(string file, int season, int episode)
    {
        MovieFile result = Resolve(file);
        result.Season.Should().Be(season);
        result.Episode.Should().Be(episode);
    }

    // ---------------------------------------------------------------------------
    // A raw disc rip is named for its track. The track index is not an episode
    // and the only other number is the volume, so all 29 tracks of two discs were
    // claiming their volume's first episode.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("The Pink Panther Volume 1_t12.mkv")]
    [InlineData("The Pink Panther Volume 2_t06.mkv")]
    public void A_disc_track_guesses_no_episode(string file)
    {
        MovieFile result = Resolve(file, @"E:\TV.Shows\The Pink Panther Volume 1");
        result.Season.Should().BeNull();
        result.Episode.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // Cutting the title at the release year split a parenthesised one in half, so
    // 524 files across nine shows searched the providers for "Fairy Tail (".
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData(["[Erai-raws] Fairy Tail - 175 [1080p].mkv", "Fairy Tail"])]
    [InlineData(["[Sokudo] Dororo (2019) - 07v2 [1080p BD][AV1][dual audio].mkv", "Dororo"])]
    public void A_title_never_keeps_half_a_bracket(string file, string title)
    {
        Resolve(file).Title.Should().Be(title);
    }

    // ---------------------------------------------------------------------------
    // A file named for its ROLE in a release has no title in it, and the show it
    // belongs to is the folder holding it. Searching a provider for the role word
    // instead is how a creditless ending became an episode of a romance series
    // and a disc menu became an episode of a cooking show — a search always
    // returns something, which is why it must not be asked.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Opening 06.mkv")] // matched "Opening Act" S01E06
    [InlineData("Ending 18.mkv")] // matched "Never-Ending Summer" S01E19
    [InlineData("Menu - 01.mkv")] // matched "Great British Menu" S01E01
    [InlineData("NCED - 01.mkv")] // matched "Flavor, NC" S01E01
    [InlineData("NCOP.mkv")]
    [InlineData("Creditless Opening 2.mkv")]
    [InlineData("- 03 -.mkv")]
    public void A_file_named_for_its_role_takes_the_title_from_its_folder(string file)
    {
        MovieFile result = Resolve(file, @"E:\Anime\Bocchi the Rock!");

        result
            .Title.Should()
            .Be(
                "Bocchi the Rock!",
                "the show is the folder — the name only says what the file is inside the release"
            );
    }

    [Fact]
    public void A_role_word_with_no_folder_to_fall_back_on_stays_unmatched()
    {
        // Nobody knows what this is. An unmatched file is one the operator can
        // place; a confident wrong answer is one they have to find first.
        Resolve("Opening 06.mkv", string.Empty).Title.Should().BeNull();
    }

    // ---------------------------------------------------------------------------
    // A BD batch names every season it packs with '+' rather than a space/dash
    // ("...Ah! My Goddess! (Aa! Megami-Sama) S1+S2+Movie+OVAs [BD]..."), so "S1"
    // was never bounded on its right and "S1+S2+Movie+OVAs" leaked into the show
    // title handed to the providers.
    // ---------------------------------------------------------------------------
    [Fact]
    public void A_plus_joined_batch_season_tag_is_dropped_from_the_folder_title()
    {
        MovieFile result = Resolve(
            "NCOP.mkv",
            @"M:\Download\done\[Tenrai-Sensei] Ah! My Goddess! (Aa! Megami-Sama) S1+S2+Movie+OVAs [BD][1080p][HEVC 10bit x265][Dual Audio]\Season 2"
        );

        result.Title.Should().Be("Ah! My Goddess!");
    }

    // ---------------------------------------------------------------------------
    // A fansub BD batch covers several seasons plus a movie and OVAs in one
    // folder, and splits the actual season into a "Season N" subfolder — the
    // release name above it still carries the plus-joined "S1+S2+Movie+OVAs"
    // tag. A flat-numbered episode file ("01.mkv") has no title of its own, so
    // both the title and the season are read from folder context: the nearest
    // "Season 2" folder supplies the season, and the release folder above it
    // — with the batch tag already stripped by the "+"-boundary fix — supplies
    // the title.
    // ---------------------------------------------------------------------------
    [Fact]
    public void A_flat_numbered_episode_in_a_batch_release_takes_season_and_title_from_its_folders()
    {
        MovieFile result = Resolve(
            "01.mkv",
            @"M:\Download\done\[Tenrai-Sensei] Ah! My Goddess! (Aa! Megami-Sama) S1+S2+Movie+OVAs [BD][1080p][HEVC 10bit x265][Dual Audio]\Season 2"
        );

        result.Title.Should().Be("Ah! My Goddess!");
        result.Season.Should().Be(2);
        result.Episode.Should().Be(1);
    }

    [Theory]
    // The guard. These are real shows whose names ARE those words, and a file
    // that names its show keeps it. The rule only fires when the role word is
    // the whole of what was found.
    [InlineData(["Opening Act - S01E06 - Tanner & Brad Paisley.mkv", "Opening Act"])]
    [InlineData(["Great British Menu - S07E02 - Scotland Fish.mkv", "Great British Menu"])]
    [InlineData(["Never-Ending Summer - S01E19 - Episode 19.mkv", "Never-Ending Summer"])]
    [InlineData(["Special A - S01E01 - Hello.mkv", "Special A"])]
    public void A_show_whose_name_contains_a_role_word_keeps_its_name(string file, string title)
    {
        Resolve(file, @"E:\TV.Shows\Whatever").Title.Should().Be(title);
    }

    // ---------------------------------------------------------------------------
    // Everything below came out of a sweep of 10,329 real release names — the
    // archived nyaa.se listings for torrent ids 1–10,000. These are not invented
    // shapes; each one is a file somebody actually released, and each was
    // resolved wrongly before the rule beneath it existed.
    // ---------------------------------------------------------------------------

    [Theory]
    // The technical block a release puts in parentheses is full of digits, and
    // only square brackets were ever stripped before an adapter read a number.
    // So the codec's own version became the episode: DivX6.61 -> episode 61,
    // DivX6.7 -> episode 7. The real number was in plain sight before "RAW".
    [InlineData(["H2O ~FOOTPRINTS IN THE SAND~ 03 RAW (1280x720 DivX6.61).avi", 3])]
    [InlineData(["Hatenkou Yuugi 03 RAW (D-KBS_704x396 24fps DivX6.7).avi", 3])]
    [InlineData(["CLANNAD 06 raw(BS-i DivX6.6 1280x720).avi", 6])]
    public void A_codec_version_is_not_an_episode_number(string file, int episode)
    {
        Resolve(file).Episode.Should().Be(episode);
    }

    [Theory]
    // What the same three files should be CALLED. A title carrying the source,
    // the codec or the resolution is a search string no catalogue holds.
    [InlineData([
        "H2O ~FOOTPRINTS IN THE SAND~ 03 RAW (1280x720 DivX6.61).avi",
        "H2O ~FOOTPRINTS IN THE SAND~",
    ])]
    [InlineData(["Hatenkou Yuugi 03 RAW (D-KBS_704x396 24fps DivX6.7).avi", "Hatenkou Yuugi"])]
    [InlineData(["CLANNAD 06 raw(BS-i DivX6.6 1280x720).avi", "CLANNAD"])]
    public void A_title_does_not_keep_the_release_it_came_from(string file, string title)
    {
        Resolve(file).Title.Should().Be(title);
    }

    [Fact]
    public void A_bracket_with_no_opening_half_does_not_ride_into_the_title()
    {
        // The group opens before the file name starts, so nothing pairs with the
        // "]" and the paired-group strip cannot see it. Searched for as
        // "AW-Raw] One Piece".
        MovieFile result = Resolve("AW-Raw]_One_Piece_310_SD.WS_(704x396)[6AF865BD].avi");

        result.Title.Should().Be("One Piece");
        result.Episode.Should().Be(310);
    }

    [Fact]
    public void A_creditless_marker_ends_the_title_rather_than_joining_it()
    {
        // The show IS named here, unlike the bare "NCED - 01" shape — it is just
        // followed by what the file is. Everything from the marker on describes
        // the extra, not the show.
        Resolve("Air TV - Creditless Opening (DVD)(AT).avi").Title.Should().Be("Air TV");
    }

    [Fact]
    public void A_name_that_is_only_punctuation_falls_back_to_the_folder()
    {
        // A directory listing's tree drawing, kept as a file name. The show's
        // real name appears only inside brackets, and every bracket is stripped,
        // so "│" was the entire title on 41 files in one archive.
        Resolve(
            "│      [00][SG][ARIA_The_Animation][00][XVID][BIG5].avi",
            @"E:\Anime\ARIA The Animation"
        )
            .Title.Should()
            .Be("ARIA The Animation");
    }

    // ---------------------------------------------------------------------------
    // A hyphen-joined pair of numbers is a SPAN, and a span starts at its first
    // number. The last number used to win, because the trailing number is the
    // episode and the leading ones are the show's name — which holds until both
    // are trailing. A file covering episodes three and four was filed as four,
    // so three read as missing and four read as a duplicate of a file that is
    // not it.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Show Name - 03-04 - Ep Name.mkv", 3)]
    [InlineData("[Group] Show Name - 01-02 [1080p].mkv", 1)]
    [InlineData("[ShinBunBu-Subs] Bleach - 02-03 (CX 1280x720 x264 AAC).mkv", 2)]
    public void A_range_resolves_to_the_first_episode_it_covers(string file, int episode) =>
        Resolve(file).Episode.Should().Be(episode);

    [Fact]
    public void A_single_trailing_number_is_still_the_episode() =>
        Resolve("[Group] Fairy Tail - 175 [1080p].mkv").Episode.Should().Be(175);

    // ---------------------------------------------------------------------------
    // Season-scoped material with no episode of its own. The season number was
    // the only number in the name, so it was read as the episode and the file
    // took that slot in season one — on top of whatever real episode lived
    // there. Code Geass season two's trailer was season one, episode two.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Code Geass Season 2 Trailer [DVDrip][h264][RUS].mkv", "Code Geass", 2)]
    [InlineData(
        "[RiviAxt] Code Geass Hangyaku no Lelouch Season 2 Short Preview.mkv",
        "Code Geass Hangyaku no Lelouch",
        2
    )]
    [InlineData(
        "[Exiled-Destiny]_UFO_Ultramaiden_Valkyrie_Season_1_Recap_(AC7BFBCF).mkv",
        "UFO Ultramaiden Valkyrie",
        1
    )]
    public void A_season_with_no_episode_claims_no_episode(string file, string title, int season)
    {
        MovieFile result = Resolve(file);
        result.Title.Should().Be(title);
        result.Season.Should().Be(season);
        result.Episode.Should().BeNull();
    }

    [Fact]
    public void A_season_marker_beside_a_real_episode_is_not_a_season_pack()
    {
        // The five is still there after the season marker is taken out, so the
        // five is the episode and the marker is only context.
        MovieFile result = Resolve("[Judas] Blue Lock S01 - 05 [1080p].mkv");
        result.Episode.Should().Be(5);
    }

    // ---------------------------------------------------------------------------
    // A special numbered by word rather than beside its marker. "Movie.Part1"
    // and "Movie.Part2" both defaulted to one, so the second film was written
    // over the first at S00E01.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Fatal.Fury.-.The.Motion.Picture.-.Movie.Part1.(DVDMux).mkv", 1)]
    [InlineData("Fatal.Fury.-.The.Motion.Picture.-.Movie.Part2.(DVDMux).mkv", 2)]
    public void A_special_numbered_by_word_keeps_its_own_number(string file, int episode)
    {
        MovieFile result = Resolve(file);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(episode);
    }

    // ---------------------------------------------------------------------------
    // A numbered episode whose TITLE holds a specials word. The word matched
    // anywhere in the name and nothing looked at the number already set off
    // before it, so "Show Name - 12 - The Special" was filed as S00E01 — the slot
    // an unnumbered marker defaults to — on top of every other such episode of
    // the show, while the real episode 12 read as missing.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("Show Name - 12 - The Special.mkv", "Show Name", 12)]
    [InlineData("Naruto - 052 - Extra Training.mkv", "Naruto", 52)]
    [InlineData("Show Name - 07 - Movie Night.mkv", "Show Name", 7)]
    public void A_numbered_episode_whose_title_holds_a_specials_word_keeps_its_number(
        string file,
        string title,
        int episode
    )
    {
        MovieFile result = Resolve(file, @"E:\TV.Shows\Show Name", "tv");
        result.Episode.Should().Be(episode);
        result.Season.Should().NotBe(0);
        result.Title.Should().Be(title);
    }

    [Theory]
    // The guard. A number inside the show's own name has no dash of its own, and
    // the parenthesised year is gone before any adapter reads a number. Neither
    // is an episode, so the bare marker after them is still the first special.
    [InlineData("Mob Psycho 100 - Special.mkv", "Mob Psycho 100")]
    [InlineData("Dororo (2019) - Special.mkv", "Dororo")]
    public void A_number_inside_the_show_name_does_not_unmake_a_special(string file, string title)
    {
        MovieFile result = Resolve(file, @"E:\TV.Shows\Whatever", "tv");
        result.Title.Should().Be(title);
        result.Season.Should().Be(0);
        result.Episode.Should().Be(1);
    }

    // ---------------------------------------------------------------------------
    // The tracker that repackaged a release stamps its own address on the front.
    // It is not part of the show's name, and a search for "www Torrenting com -
    // Show Name" answers with whatever scores least badly.
    //
    // Only an address is cut, and only at the front. A hardcoded list of tracker
    // tags is what the old parsers kept, and it needs a new entry every time a
    // site appears; an address is recognisable by being one.
    // ---------------------------------------------------------------------------
    [Theory]
    [InlineData("www.Torrenting.com.-.Show.Name.S01E02.mkv", "Show Name")]
    [InlineData("[ www.TorrentDay.com ] - Show.Name.S01E02.mkv", "Show Name")]
    [InlineData(".www.Cpasbien.pw.Show.Name.S01E02.mkv", "Show Name")]
    [InlineData("{ www.SceneTime.com } - Show.Name.S01E02.mkv", "Show Name")]
    public void A_tracker_address_is_not_part_of_the_show_name(string file, string title) =>
        Resolve(file).Title.Should().Be(title);

    [Theory]
    [InlineData("Back.to.the.Future.S01E02.mkv", "Back to the Future")]
    [InlineData("Halt.and.Catch.Fire.S01E02.mkv", "Halt and Catch Fire")]
    [InlineData("Bob.Hearts.Abishola.S01E02.mkv", "Bob Hearts Abishola")]
    public void A_title_that_merely_looks_domain_shaped_is_left_alone(string file, string title) =>
        Resolve(file).Title.Should().Be(title);

    [Fact]
    public void A_part_glued_to_a_word_is_not_a_part_marker()
    {
        // "part3" here is inside the episode's Japanese title, and the real
        // marker is the 第11話 before it. A marker glued to the end of a word is
        // not a marker — in any script, which is why the boundary is every
        // letter rather than the ASCII ones.
        MovieFile result = Resolve(
            "(アニメ) スケッチブック ～full color's～ 第11話 「風邪の日と、ねこねこpart3」(1280x720 x264 AAC).mkv"
        );
        result.Episode.Should().Be(11);
    }
}
