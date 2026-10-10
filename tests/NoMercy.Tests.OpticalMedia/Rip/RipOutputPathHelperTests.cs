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

using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Rip;
using NoMercy.OpticalMedia.Sources;

namespace NoMercy.Tests.OpticalMedia.Rip;

/// <summary>
/// REQUIREMENT: <see cref="RipOutputPathHelper.Build"/> must derive the
/// folder-relative rip destination the folder-watcher import pipeline can
/// match against TMDB — movies get <c>{Title} ({Year})/...</c>, TV/anime get
/// a per-season folder with <c>S{SS}E{EE}</c> naming (episode number offset
/// by <c>batchIndex</c>), unknown library types and missing metadata always
/// fall back to <c>disc-rips/title_NN.mkv</c>, and titles are sanitized for
/// filesystem-invalid characters.
/// </summary>
[Trait("Category", "Unit")]
public class RipOutputPathHelperTests
{
    private static RipRequest MakeRequest(CustomMetadata? custom) =>
        new(
            DrivePath: "D:\\",
            SelectedTitleIndices: [1],
            MetadataId: null,
            Custom: custom,
            LibraryId: Ulid.NewUlid(),
            FolderId: Ulid.NewUlid(),
            EncodingProfileId: null,
            AudioTracks: [],
            Subtitles: []
        );

    [Fact]
    public void Build_NoCustomMetadata_FallsBackToDiscRipsTitleIndex()
    {
        string path = RipOutputPathHelper.Build(MakeRequest(custom: null), "movie", 3, 0);

        path.Should().Be("disc-rips/title_03.mkv");
    }

    [Fact]
    public void Build_WhitespaceOnlyTitle_FallsBackToDiscRipsTitleIndex()
    {
        CustomMetadata custom = new("   ", 2024, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "movie", 5, 0);

        path.Should().Be("disc-rips/title_05.mkv");
    }

    [Fact]
    public void Build_UnknownLibraryType_FallsBackToDiscRipsTitleIndex()
    {
        CustomMetadata custom = new("Some Movie", 2024, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "music", 7, 0);

        path.Should().Be("disc-rips/title_07.mkv");
    }

    [Theory]
    [InlineData("movie", 0, "Inception (2010)/Inception (2010).mkv")]
    [InlineData("movie", 1, "Inception (2010)/Inception (2010) - Disc 2.mkv")]
    [InlineData("movie", 2, "Inception (2010)/Inception (2010) - Disc 3.mkv")]
    public void Build_MovieLibraryType_BuildsShowRootAndDiscSuffix(
        string libraryType,
        int batchIndex,
        string expected
    )
    {
        CustomMetadata custom = new("Inception", 2010, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(
            MakeRequest(custom),
            libraryType,
            titleIndex: 1,
            batchIndex
        );

        path.Should().Be(expected);
    }

    [Fact]
    public void Build_MovieWithoutYear_OmitsYearSuffix()
    {
        CustomMetadata custom = new("Untitled", null, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "movie", 1, 0);

        path.Should().Be("Untitled/Untitled.mkv");
    }

    [Theory]
    [InlineData("tv")]
    [InlineData("anime")]
    public void Build_TvOrAnimeLibraryType_BuildsSeasonFolderAndSxxExx(string libraryType)
    {
        CustomMetadata custom = new(
            "Breaking Bad",
            2008,
            MediaType.TvShow,
            null,
            SeasonNumber: 2,
            EpisodeStartNumber: 5
        );

        string path = RipOutputPathHelper.Build(
            MakeRequest(custom),
            libraryType,
            titleIndex: 1,
            batchIndex: 0
        );

        path.Should().Be("Breaking Bad (2008)/Season 02/Breaking Bad S02E05.mkv");
    }

    [Fact]
    public void Build_TvShow_BatchIndexOffsetsEpisodeNumber()
    {
        CustomMetadata custom = new(
            "Show",
            2020,
            MediaType.TvShow,
            null,
            SeasonNumber: 1,
            EpisodeStartNumber: 1
        );

        string path = RipOutputPathHelper.Build(
            MakeRequest(custom),
            "tv",
            titleIndex: 1,
            batchIndex: 3
        );

        path.Should().Be("Show (2020)/Season 01/Show S01E04.mkv");
    }

    [Fact]
    public void Build_TvShow_NoSeasonNumber_DefaultsToSeasonOne()
    {
        CustomMetadata custom = new("Show", 2020, MediaType.TvShow, null, SeasonNumber: null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "tv", 1, 0);

        path.Should().Contain("Season 01");
    }

    [Fact]
    public void Build_TvShow_NoEpisodeStartNumber_DefaultsToEpisodeOne()
    {
        CustomMetadata custom = new(
            "Show",
            2020,
            MediaType.TvShow,
            null,
            SeasonNumber: 1,
            EpisodeStartNumber: null
        );

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "tv", 1, 0);

        path.Should().Contain("S01E01");
    }

    [Theory]
    [InlineData("Rocky: A Story?", "Rocky A Story")]
    [InlineData("Colon:Test", "Colon Test")]
    [InlineData("Slash/Back\\Slash", "Slash Back Slash")]
    [InlineData("Pipe|Question?Star*", "Pipe Question Star")]
    [InlineData("Quote\"Angle<>Bracket", "Quote Angle Bracket")]
    public void Build_SanitizesFilesystemInvalidCharsFromTitle(string rawTitle, string sanitized)
    {
        CustomMetadata custom = new(rawTitle, 2020, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "movie", 1, 0);

        path.Should().Be($"{sanitized} (2020)/{sanitized} (2020).mkv");
    }

    [Fact]
    public void Build_CollapsesMultipleWhitespaceRunsToSingleSpace()
    {
        CustomMetadata custom = new("Too    Many     Spaces", 2020, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "movie", 1, 0);

        path.Should().Be("Too Many Spaces (2020)/Too Many Spaces (2020).mkv");
    }

    [Fact]
    public void Build_TrimsSanitizedTitle()
    {
        CustomMetadata custom = new("  Padded Title  ", 2020, MediaType.Movie, null);

        string path = RipOutputPathHelper.Build(MakeRequest(custom), "movie", 1, 0);

        path.Should().Be("Padded Title (2020)/Padded Title (2020).mkv");
    }
}
