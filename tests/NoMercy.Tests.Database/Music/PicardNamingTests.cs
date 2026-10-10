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

using NoMercy.Database.Music;

namespace NoMercy.Tests.Database.Music;

/// <summary>
/// The expectations below are what Stoney's Picard script produces, so they describe a
/// library that already exists on disk. A change that "improves" a name here silently
/// splits that library into two conventions.
/// </summary>
[Trait("Category", "Unit")]
public class PicardNamingTests
{
    private static MusicNamingContext JoshuaTree() =>
        new()
        {
            AlbumName = "The Joshua Tree",
            Year = 1987,
            AlbumArtistId = "a3cb23fc-acd3-4ce0-8f36-1e5aa6a18432",
            AlbumArtistSort = "U2",
            AlbumArtistPrimary = "U2",
            TrackTitle = "Where the Streets Have No Name",
            TrackArtistPrimary = "U2",
            TrackArtistsCredited = "U2",
            TrackNumber = 1,
            TotalTracks = 11,
        };

    [Fact]
    public void A_standard_album_files_under_initial_artist_and_bracketed_year()
    {
        Assert.Equal(
            "U/U2/[1987] The Joshua Tree/01 Where the Streets Have No Name",
            PicardNaming.BuildPath(JoshuaTree())
        );
    }

    [Fact]
    public void A_track_by_the_album_artist_carries_no_suffix()
    {
        Assert.Equal("01 Where the Streets Have No Name", PicardNaming.BuildFileName(JoshuaTree()));
    }

    [Fact]
    public void A_guest_on_the_album_artists_own_track_is_credited_as_feat()
    {
        MusicNamingContext context = JoshuaTree() with { TrackArtistsAdditional = "Brian Eno" };

        Assert.Equal(
            "01 Where the Streets Have No Name [feat. Brian Eno]",
            PicardNaming.BuildFileName(context)
        );
    }

    /// <summary>
    /// A different performer is not a guest — the whole credit is shown, which is what
    /// makes a compilation readable in a file browser.
    /// </summary>
    [Fact]
    public void A_track_by_someone_else_shows_the_whole_credit()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            TrackArtistPrimary = "Johnny Cash",
            TrackArtistsCredited = "Johnny Cash",
        };

        Assert.Equal(
            "01 Where the Streets Have No Name [Johnny Cash]",
            PicardNaming.BuildFileName(context)
        );
    }

    /// <summary>
    /// The script compares artists with the leading article removed, so "The Beatles"
    /// billed as "Beatles" is still the album artist and earns no suffix.
    /// </summary>
    [Fact]
    public void A_leading_article_does_not_make_the_artist_a_guest()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumArtistPrimary = "The Beatles",
            TrackArtistPrimary = "Beatles",
            TrackArtistsCredited = "Beatles",
        };

        Assert.Equal("01 Where the Streets Have No Name", PicardNaming.BuildFileName(context));
    }

    [Fact]
    public void A_multi_disc_release_prefixes_the_disc()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            DiscNumber = 2,
            TotalDiscs = 3,
            TrackNumber = 7,
        };

        Assert.Equal("2-07 Where the Streets Have No Name", PicardNaming.BuildFileName(context));
    }

    [Fact]
    public void Track_numbers_pad_to_the_width_of_the_total()
    {
        MusicNamingContext context = JoshuaTree() with { TrackNumber = 7, TotalTracks = 120 };

        Assert.Equal("007 Where the Streets Have No Name", PicardNaming.BuildFileName(context));
    }

    [Fact]
    public void A_various_artists_release_files_under_its_own_bucket()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumArtistId = PicardNaming.VariousArtistId,
            AlbumName = "A State of Trance 2008",
        };

        Assert.StartsWith(
            "[Various Artists]/[1987] A State of Trance 2008/",
            PicardNaming.BuildPath(context)
        );
    }

    [Fact]
    public void An_unidentified_album_artist_files_under_the_unknown_bucket()
    {
        MusicNamingContext context = JoshuaTree() with { AlbumArtistId = null };

        Assert.StartsWith(
            "[Unknown Artist]/[1987] The Joshua Tree/",
            PicardNaming.BuildPath(context)
        );
    }

    [Fact]
    public void A_soundtrack_files_under_soundtracks_and_always_shows_the_performer()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumType = MusicAlbumType.Soundtrack,
            AlbumName = "Blade Runner",
            TrackArtistsCredited = "Vangelis",
        };

        Assert.Equal(
            "[Soundtracks]/[1987] Blade Runner/01 Where the Streets Have No Name [Vangelis]",
            PicardNaming.BuildPath(context)
        );
    }

    [Fact]
    public void A_single_files_under_the_artists_singles_folder_and_leads_with_the_year()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumType = MusicAlbumType.Single,
            TrackTitle = "One",
        };

        Assert.Equal("U/U2/[Singles]/[1987] One", PicardNaming.BuildPath(context));
    }

    [Fact]
    public void A_classical_release_puts_the_performer_before_the_title()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumType = MusicAlbumType.Classical,
            AlbumName = "The Four Seasons",
            TrackTitle = "Spring",
            TrackArtistPrimary = "Itzhak Perlman",
        };

        Assert.Equal(
            "[Classical]/[1987] The Four Seasons/01 [Itzhak Perlman] Spring",
            PicardNaming.BuildPath(context)
        );
    }

    [Fact]
    public void An_artist_starting_with_a_digit_files_under_hash()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumArtistSort = "2Pac",
            AlbumArtistPrimary = "2Pac",
        };

        Assert.StartsWith("#/2Pac/", PicardNaming.BuildPath(context));
    }

    [Fact]
    public void The_artist_folder_uses_the_display_name_not_the_sort_name()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumArtistSort = "Springsteen, Bruce",
            AlbumArtistPrimary = "Bruce Springsteen",
        };

        Assert.StartsWith("B/Bruce Springsteen/", PicardNaming.BuildPath(context));
    }

    [Fact]
    public void A_missing_year_becomes_four_zeroes_rather_than_disappearing()
    {
        MusicNamingContext context = JoshuaTree() with { Year = null };

        Assert.Contains("[0000] The Joshua Tree", PicardNaming.BuildPath(context));
    }

    /// <summary>
    /// Sanitization runs once over the joined path, so the separators the rules produced
    /// survive while a colon inside a title does not.
    /// </summary>
    [Fact]
    public void Unsafe_characters_collapse_to_a_single_underscore_without_touching_separators()
    {
        MusicNamingContext context = JoshuaTree() with
        {
            AlbumName = "Album: The **Reckoning**",
            TrackTitle = "Track?*Name",
        };

        string sanitized = PicardNaming.Sanitize(PicardNaming.BuildPath(context));

        Assert.Equal("U/U2/[1987] Album_ The _Reckoning_/01 Track_Name", sanitized);
    }

    /// <summary>
    /// U+2019 reproducibly fails to create a directory on Stoney's NAS — confirmed
    /// identically across raw Win32 CreateDirectoryW, .NET's Directory.CreateDirectory,
    /// and PowerShell's own New-Item, and across both soft and hard NFS mount modes. The
    /// ASCII apostrophe is filesystem-safe everywhere and stands in for it on disk.
    /// </summary>
    [Fact]
    public void A_curly_apostrophe_folds_to_ascii_so_the_folder_actually_persists()
    {
        MusicNamingContext context = JoshuaTree() with { AlbumName = "I’m Into You" };

        string sanitized = PicardNaming.Sanitize(PicardNaming.BuildPath(context));

        Assert.Contains("I'm Into You", sanitized);
        Assert.DoesNotContain('’', sanitized);
    }

    /// <summary>
    /// Curly quotes fold to straight ASCII quotes for the same reason the curly
    /// apostrophe does — but a straight <c>"</c> is itself one of Windows' reserved path
    /// characters, so the fold alone would trade one CreateDirectory failure for another.
    /// Sanitize has to strip what its own unicode fold produces, not just what the source
    /// title carried.
    /// </summary>
    [Fact]
    public void A_curly_quote_fold_does_not_leave_a_reserved_straight_quote_behind()
    {
        MusicNamingContext context = JoshuaTree() with { AlbumName = "“Famous Last Words”" };

        string sanitized = PicardNaming.Sanitize(PicardNaming.BuildPath(context));

        Assert.DoesNotContain('"', sanitized);
    }

    /// <summary>
    /// A pipe is reserved on Windows but was never in the original Picard script's
    /// collapse set — "Dusty House | Room 1" reached CreateDirectory unfolded and failed.
    /// </summary>
    [Fact]
    public void A_pipe_in_the_title_is_stripped_like_the_other_reserved_characters()
    {
        MusicNamingContext context = JoshuaTree() with { AlbumName = "Dusty House | Room 1" };

        string sanitized = PicardNaming.Sanitize(PicardNaming.BuildPath(context));

        Assert.DoesNotContain('|', sanitized);
    }

    /// <summary>
    /// Every accented Latin letter reproducibly fails the same way as the curly
    /// apostrophe — confirmed live for é ñ ü à ö ç í ó ø å. NFD-decompose-and-strip
    /// folds any base+diacritic letter without a per-character entry; ø is Unicode's
    /// own counter-example (a distinct letter, not base+diacritic) and needs the
    /// explicit map alongside it.
    /// </summary>
    [Theory]
    [InlineData("Números", "Numeros")]
    [InlineData("Café del Mar", "Cafe del Mar")]
    [InlineData("Mötley Crüe", "Motley Crue")]
    [InlineData("Björk", "Bjork")]
    [InlineData("Sigur Rós", "Sigur Ros")]
    [InlineData("Øystein", "Oystein")]
    public void An_accented_or_non_decomposing_letter_folds_to_its_ascii_form(
        string original,
        string expectedAscii
    )
    {
        MusicNamingContext context = JoshuaTree() with { AlbumName = original };

        string sanitized = PicardNaming.Sanitize(PicardNaming.BuildPath(context));

        Assert.Contains(expectedAscii, sanitized);
    }

    [Fact]
    public void An_overlong_title_is_trimmed_with_an_ellipsis()
    {
        MusicNamingContext context = JoshuaTree() with { TrackTitle = new('a', 200) };

        string fileName = PicardNaming.BuildFileName(context);

        Assert.Equal(128, fileName.Length);
        Assert.EndsWith("...", fileName);
    }
}
