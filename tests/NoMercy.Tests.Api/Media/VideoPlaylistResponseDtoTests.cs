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

using NoMercy.Api.DTOs.Media;
using NoMercy.Database;
using NoMercy.Database.Models.Common;
using NoMercy.Database.Models.Media;
using NoMercy.Database.Models.Movies;
using NoMercy.Database.Models.TvShows;
using NoMercy.Database.Models.Users;
using Xunit;

namespace NoMercy.Tests.Api.Media;

[Trait("Category", "Playlist")]
public class VideoPlaylistResponseDtoTests
{
    private static Episode BuildEpisodeWithVideoFile(
        VideoTrack[]? tracks = null,
        string filename = "episode.mkv",
        string? subtitlesJson = null,
        UserData? userData = null,
        Image[]? tvImages = null,
        Certification[]? certifications = null,
        string languagesJson = "[\"en\"]",
        Translation? tvTranslation = null,
        Translation? episodeTranslation = null
    )
    {
        Tv tv = new()
        {
            Id = 1399,
            Title = "Breaking Bad",
            TitleSort = "breaking bad",
            FirstAirDate = new DateTime(2008, 1, 20),
        };
        foreach (Image image in tvImages ?? [])
            tv.Images.Add(image);
        foreach (Certification certification in certifications ?? [])
            tv.CertificationTvs.Add(
                new()
                {
                    CertificationId = certification.Id,
                    Certification = certification,
                    TvId = tv.Id,
                    Tv = tv,
                }
            );
        if (tvTranslation is not null)
            tv.Translations.Add(tvTranslation);

        Season season = new()
        {
            Id = 3572,
            Title = "Season 1",
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
        };
        Episode episode = new()
        {
            Id = 62085,
            Title = "Pilot",
            Overview = "A struggling teacher is diagnosed with cancer.",
            EpisodeNumber = 1,
            SeasonNumber = 1,
            TvId = tv.Id,
            Tv = tv,
            SeasonId = season.Id,
            Season = season,
        };
        if (episodeTranslation is not null)
            episode.Translations.Add(episodeTranslation);

        VideoFile videoFile = new()
        {
            Filename = filename,
            Folder = "/tv",
            HostFolder = "/tv",
            Languages = languagesJson,
            Quality = "1080p",
            Share = "tv",
            EpisodeId = episode.Id,
            Episode = episode,
            Tracks = tracks ?? [],
            Subtitles = subtitlesJson,
        };
        if (userData is not null)
            videoFile.UserData.Add(userData);
        episode.VideoFiles.Add(videoFile);

        return episode;
    }

    private static Movie BuildMovieWithCertification(
        string countryIso,
        VideoTrack[]? tracks = null,
        string languagesJson = "[\"en\"]",
        Translation? translation = null
    )
    {
        Certification certification = new()
        {
            Id = 1,
            Iso31661 = countryIso,
            Rating = "PG-13",
            Meaning = "Parental guidance",
            Order = 1,
        };

        Movie movie = new()
        {
            Id = 42,
            Title = "Test Movie",
            TitleSort = "test movie",
            Overview = "A movie used for DTO tests.",
        };
        if (translation is not null)
            movie.Translations.Add(translation);

        movie.CertificationMovies.Add(
            new()
            {
                CertificationId = certification.Id,
                Certification = certification,
                MovieId = movie.Id,
                Movie = movie,
            }
        );

        movie.VideoFiles.Add(
            new()
            {
                Filename = "test-movie.mkv",
                Folder = "/test",
                HostFolder = "/test",
                Languages = languagesJson,
                Quality = "1080p",
                Share = "movies",
                MovieId = movie.Id,
                Movie = movie,
                Tracks = tracks ?? [],
            }
        );

        return movie;
    }

    [Fact]
    public void Ctor_PlainMovie_NoIndex_StillSetsContentRating()
    {
        Movie movie = BuildMovieWithCertification("US");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        Assert.NotNull(dto.ContentRating);
        Assert.Equal("PG-13", dto.ContentRating!.Rating);
    }

    [Fact]
    public void Ctor_CollectionMovie_WithIndex_SetsContentRatingAndCollectionFields()
    {
        Movie movie = BuildMovieWithCertification("US");

        VideoPlaylistResponseDto dto = new(movie, "collection", 1, "US", index: 2);

        Assert.NotNull(dto.ContentRating);
        Assert.Equal("PG-13", dto.ContentRating!.Rating);
        Assert.Equal(0, dto.Season);
        Assert.Equal(2, dto.Episode);
        Assert.Equal("Collection", dto.SeasonName);
    }

    [Fact]
    public void Ctor_SpriteKindTrack_StaysSprite()
    {
        Movie movie = BuildMovieWithCertification(
            "US",
            [new() { File = "/sprite.webp", Kind = "sprite" }]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        Assert.Contains(dto.Tracks, track => track.Kind == "sprite");
        Assert.DoesNotContain(dto.Tracks, track => track.Kind == "thumbnails");
    }

    [Fact]
    public void Ctor_ThumbnailsKindTrack_StaysThumbnails()
    {
        Movie movie = BuildMovieWithCertification(
            "US",
            [new() { File = "/thumbs_320x178.vtt", Kind = "thumbnails" }]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        VideoTrack track = Assert.Single(dto.Tracks, t => t.Kind == "thumbnails");
        Assert.EndsWith(".vtt", track.File);
    }

    [Fact]
    public void Ctor_SpriteAndThumbnailsBothPresent_BothReachTheClient()
    {
        // The pair is the feature: the Android and TV players read the sheet from
        // the sprite track and the cue times from the thumbnails track, and show no
        // previews at all unless they have both. Dropping either half here is what
        // silently removed scrub previews from every title that had them.
        Movie movie = BuildMovieWithCertification(
            "US",
            [
                new() { File = "/thumbs_320x178.webp", Kind = "sprite" },
                new() { File = "/thumbs_320x178.vtt", Kind = "thumbnails" },
            ]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        Assert.EndsWith(".webp", Assert.Single(dto.Tracks, t => t.Kind == "sprite").File);
        Assert.EndsWith(".vtt", Assert.Single(dto.Tracks, t => t.Kind == "thumbnails").File);
    }

    [Fact]
    public void Ctor_StaleSpriteRow_IsReplacedByTheNamesTheScanRecorded()
    {
        // A folder scanned before the preview files were renamed keeps a sprite
        // row naming a sheet that is no longer on disk and no thumbnails row at
        // all, and both clients resolve the scrub preview from the thumbnails
        // entry alone — so the strip falls back to bare timestamps. Measured on a
        // real television against Furious.7.(2015): the row said sprite.webp and
        // the folder held thumbs_320x180.webp beside its .vtt.
        Movie movie = BuildMovieWithCertification(
            "US",
            [new() { File = "/sprite.webp", Kind = "sprite" }]
        );

        movie.VideoFiles.First().Metadata = new()
        {
            Previews =
            [
                new()
                {
                    ImageFileName = "thumbs_320x180.webp",
                    ImageFileSize = 1024,
                    TimeFileName = "thumbs_320x180.vtt",
                    TimeFileSize = 512,
                },
            ],
        };

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        Assert.EndsWith(
            "thumbs_320x180.vtt",
            Assert.Single(dto.Tracks, t => t.Kind == "thumbnails").File
        );
        Assert.EndsWith(
            "thumbs_320x180.webp",
            Assert.Single(dto.Tracks, t => t.Kind == "sprite").File
        );
    }

    [Fact]
    public void Ctor_MultipleSpriteTracks_AllSurvive()
    {
        // A title rendered at more than one preview dimension has a sheet per size.
        // Choosing one for the client is the client's business, not the payload's.
        Movie movie = BuildMovieWithCertification(
            "US",
            [
                new()
                {
                    File = "/first.webp",
                    Kind = "sprite",
                    Label = "first",
                },
                new()
                {
                    File = "/second.webp",
                    Kind = "sprite",
                    Label = "second",
                },
            ]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Tracks.Should().HaveCount(2).And.OnlyContain(t => t.Kind == "sprite");
    }

    [Fact]
    public void Ctor_PreviewTracksAlongsideUnrelatedKind_LeaveTheUnrelatedKindAlone()
    {
        Movie movie = BuildMovieWithCertification(
            "US",
            [
                new() { File = "/first.webp", Kind = "sprite" },
                new() { File = "/second.webp", Kind = "sprite" },
                new() { File = "/audio.m4a", Kind = "audio" },
            ]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Tracks.Should().HaveCount(3);
        dto.Tracks.Count(t => t.Kind == "sprite").Should().Be(2);
        dto.Tracks.Should().ContainSingle(t => t.Kind == "audio");
    }

    [Fact]
    public void Ctor_SingleThumbnailsTrackAlongsideUnrelatedKind_BothPreserved()
    {
        // NormalizePreviewTracks only dedupes when 2+ tracks resolve to "thumbnails".
        // A lone thumbnails track sharing space with an unrelated kind must return
        // early and leave both untouched.
        Movie movie = BuildMovieWithCertification(
            "US",
            [
                new() { File = "/thumbs.vtt", Kind = "thumbnails" },
                new() { File = "/audio.m4a", Kind = "audio" },
            ]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Tracks.Should().Contain(t => t.Kind == "thumbnails");
        dto.Tracks.Should().Contain(t => t.Kind == "audio");
    }

    [Fact]
    public void Ctor_NonSubtitleTrackFile_IsPrefixedWithVideoFileBaseFolder()
    {
        Movie movie = BuildMovieWithCertification(
            "US",
            [new() { File = "/thumbs.vtt", Kind = "thumbnails" }]
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        VideoTrack track = dto.Tracks.Single(t => t.Kind == "thumbnails");
        track.File.Should().Be("/movies/test/thumbs.vtt");
    }

    [Fact]
    public void Ctor_Movie_Mp4Filename_UsesVideoMp4SourceType()
    {
        Movie movie = BuildMovieWithCertification("US");
        movie.VideoFiles.First().Filename = "test-movie.mp4";

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Sources.Should().ContainSingle();
        dto.Sources[0].Type.Should().Be("video/mp4");
    }

    [Fact]
    public void Ctor_Movie_LanguagesArrayContainsNullEntry_FiltersItOut()
    {
        Movie movie = BuildMovieWithCertification("US", languagesJson: "[\"en\", null]");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Sources[0].Languages.Should().Equal("en");
    }

    [Fact]
    public void Ctor_Movie_LanguagesJsonIsNullLiteral_YieldsEmptyLanguagesArray()
    {
        Movie movie = BuildMovieWithCertification("US", languagesJson: "null");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Sources[0].Languages.Should().BeEmpty();
    }

    [Fact]
    public void Ctor_Movie_TranslationPresent_OverridesTitleAndOverview()
    {
        Movie movie = BuildMovieWithCertification(
            "US",
            translation: new()
            {
                Iso6391 = "nl",
                Title = "Nederlandse titel",
                Overview = "Nederlandse samenvatting.",
            }
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Title.Should().Be("Nederlandse titel");
        dto.Description.Should().Be("Nederlandse samenvatting.");
    }

    [Fact]
    public void Ctor_Movie_Certification_MatchesUsExplicitly()
    {
        Movie movie = BuildMovieWithCertification("US");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "NL");

        dto.ContentRating.Should().NotBeNull();
        dto.ContentRating!.Iso31661.Should().Be("US");
    }

    [Fact]
    public void Ctor_Movie_NoIndex_LeavesCollectionOnlyFieldsNull()
    {
        Movie movie = BuildMovieWithCertification("US");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.SeasonName.Should().BeNull();
        dto.Season.Should().BeNull();
        dto.Episode.Should().BeNull();
    }

    [Fact]
    public void Ctor_Movie_NoVideoFile_Throws()
    {
        // A movie with no playable video file must never produce a half-built
        // DTO (null Title, zero Id) -- that hollow object is what crashed the
        // player with a blank, untitled card. The constructor now refuses to
        // build one; callers that need to skip it use TryCreate instead.
        Movie movie = new()
        {
            Id = 99,
            Title = "No File",
            TitleSort = "no file",
        };

        Action act = () => new VideoPlaylistResponseDto(movie, "movie", 1, "US");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryCreate_Movie_NoVideoFile_ReturnsNull()
    {
        Movie movie = new()
        {
            Id = 99,
            Title = "No File",
            TitleSort = "no file",
        };

        VideoPlaylistResponseDto? dto = VideoPlaylistResponseDto.TryCreate(movie, "movie", 1, "US");

        dto.Should().BeNull();
    }

    [Fact]
    public void Ctor_Movie_CollectionProvided_UsesCollectionIdAsTmdbId()
    {
        Movie movie = BuildMovieWithCertification("US");
        Collection collection = new() { Id = 777, Title = "Franchise" };

        VideoPlaylistResponseDto dto = new(
            movie,
            "collection",
            1,
            "US",
            index: 1,
            collection: collection
        );

        dto.TmdbId.Should().Be(777);
    }

    [Fact]
    public void Ctor_Movie_Certification_NoUsOrCountryMatch_LeavesContentRatingNull()
    {
        Movie movie = BuildMovieWithCertification("DE");

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "NL");

        dto.ContentRating.Should().BeNull();
    }

    [Fact]
    public void Ctor_Movie_LogoPicksHighestVoteAverageLogoImage()
    {
        Movie movie = BuildMovieWithCertification("US");
        movie.Images.Add(
            new()
            {
                Type = "logo",
                FilePath = "/low.png",
                VoteAverage = 1,
            }
        );
        movie.Images.Add(
            new()
            {
                Type = "logo",
                FilePath = "/high.png",
                VoteAverage = 9,
            }
        );
        movie.Images.Add(
            new()
            {
                Type = "poster",
                FilePath = "/poster.png",
                VoteAverage = 99,
            }
        );

        VideoPlaylistResponseDto dto = new(movie, "movie", 1, "US");

        dto.Logo.Should().Be("/high.png");
    }

    [Fact]
    public void Ctor_Episode_NoVideoFile_Throws()
    {
        Tv tv = new()
        {
            Id = 1,
            Title = "Show",
            TitleSort = "show",
        };
        Episode episode = new()
        {
            Id = 5,
            Tv = tv,
            TvId = tv.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };

        Action act = () => new VideoPlaylistResponseDto(episode, "tv", 1, "US");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryCreate_Episode_NoVideoFile_ReturnsNull()
    {
        Tv tv = new()
        {
            Id = 1,
            Title = "Show",
            TitleSort = "show",
        };
        Episode episode = new()
        {
            Id = 5,
            Tv = tv,
            TvId = tv.Id,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };

        VideoPlaylistResponseDto? dto = VideoPlaylistResponseDto.TryCreate(episode, "tv", 1, "US");

        dto.Should().BeNull();
    }

    [Fact]
    public void Ctor_Episode_NoTv_Throws()
    {
        Episode episode = new()
        {
            Id = 6,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };
        episode.VideoFiles.Add(
            new()
            {
                Filename = "x.mkv",
                Folder = "/tv",
                HostFolder = "/tv",
                Languages = "[\"en\"]",
                Quality = "1080p",
                Share = "tv",
                EpisodeId = episode.Id,
                Episode = episode,
            }
        );

        Action act = () => new VideoPlaylistResponseDto(episode, "tv", 1, "US");

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void TryCreate_Episode_NoTv_ReturnsNull()
    {
        Episode episode = new()
        {
            Id = 6,
            SeasonNumber = 1,
            EpisodeNumber = 1,
        };
        episode.VideoFiles.Add(
            new()
            {
                Filename = "x.mkv",
                Folder = "/tv",
                HostFolder = "/tv",
                Languages = "[\"en\"]",
                Quality = "1080p",
                Share = "tv",
                EpisodeId = episode.Id,
                Episode = episode,
            }
        );

        VideoPlaylistResponseDto? dto = VideoPlaylistResponseDto.TryCreate(episode, "tv", 1, "US");

        dto.Should().BeNull();
    }

    [Fact]
    public void Ctor_Episode_WithIndex_UsesSpecialTitleFormatAndHidesShow()
    {
        Episode episode = BuildEpisodeWithVideoFile();

        VideoPlaylistResponseDto dto = new(episode, "specials", 1, "US", index: 3);

        dto.Title.Should().Be("Breaking Bad %S1 %E1 - Pilot");
        dto.Show.Should().BeNull();
        dto.Season.Should().Be(0);
        dto.Episode.Should().Be(3);
    }

    [Fact]
    public void Ctor_Episode_WithoutIndex_UsesPlainTitleAndShowsShowName()
    {
        Episode episode = BuildEpisodeWithVideoFile();

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Title.Should().Be("Pilot");
        dto.Show.Should().Be("Breaking Bad");
        dto.Season.Should().Be(1);
        dto.Episode.Should().Be(1);
        dto.SeasonName.Should().Be("Season 1");
        dto.EpisodeId.Should().Be(episode.Id);
    }

    [Fact]
    public void Ctor_Episode_LogoPicksHighestVoteAverageLogoImage()
    {
        Episode episode = BuildEpisodeWithVideoFile(
            tvImages:
            [
                new()
                {
                    Type = "logo",
                    FilePath = "/low.png",
                    VoteAverage = 1,
                },
                new()
                {
                    Type = "logo",
                    FilePath = "/high.png",
                    VoteAverage = 9,
                },
                new()
                {
                    Type = "poster",
                    FilePath = "/poster.png",
                    VoteAverage = 99,
                },
            ]
        );

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Logo.Should().Be("/high.png");
    }

    [Fact]
    public void Ctor_Episode_ProgressPopulatedFromVideoFileUserData()
    {
        UserData userData = new()
        {
            Type = "tv",
            Time = 500,
            LastPlayedDate = "2026-01-15T10:00:00Z",
        };
        Episode episode = BuildEpisodeWithVideoFile(userData: userData);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Progress.Should().NotBeNull();
        dto.Progress!.Time.Should().Be(500);
    }

    [Fact]
    public void Ctor_Episode_NoUserData_ProgressIsNull()
    {
        Episode episode = BuildEpisodeWithVideoFile();

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Progress.Should().BeNull();
    }

    [Fact]
    public void Ctor_Episode_Certification_MatchesUsRegardlessOfRequestedCountry()
    {
        Certification us = new()
        {
            Id = 4,
            Iso31661 = "US",
            Rating = "TV-14",
            Meaning = "Parental guidance",
            Order = 1,
        };
        Episode episode = BuildEpisodeWithVideoFile(certifications: [us]);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "NL");

        dto.ContentRating.Should().NotBeNull();
        dto.ContentRating!.Iso31661.Should().Be("US");
    }

    [Fact]
    public void Ctor_Episode_TranslationsPresent_OverrideTitleAndOverview()
    {
        Episode episode = BuildEpisodeWithVideoFile(
            tvTranslation: new() { Iso6391 = "nl", Title = "Zwaar Weer" },
            episodeTranslation: new()
            {
                Iso6391 = "nl",
                Title = "Proefaflevering",
                Overview = "Nederlandse samenvatting.",
            }
        );

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Show.Should().Be("Zwaar Weer");
        dto.Title.Should().Be("Proefaflevering");
        dto.Description.Should().Be("Nederlandse samenvatting.");
    }

    [Fact]
    public void Ctor_Episode_Certification_MatchesRequestedCountry_WhenNoUsCertification()
    {
        Certification nl = new()
        {
            Id = 2,
            Iso31661 = "NL",
            Rating = "12",
            Meaning = "Parental guidance",
            Order = 1,
        };
        Episode episode = BuildEpisodeWithVideoFile(certifications: [nl]);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "NL");

        dto.ContentRating.Should().NotBeNull();
        dto.ContentRating!.Rating.Should().Be("12");
        dto.ContentRating.Iso31661.Should().Be("NL");
    }

    [Fact]
    public void Ctor_Episode_Certification_NoUsOrCountryMatch_LeavesContentRatingNull()
    {
        Certification de = new()
        {
            Id = 3,
            Iso31661 = "DE",
            Rating = "16",
            Meaning = "Parental guidance",
            Order = 1,
        };
        Episode episode = BuildEpisodeWithVideoFile(certifications: [de]);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "NL");

        dto.ContentRating.Should().BeNull();
    }

    [Fact]
    public void Ctor_Episode_ValidSubtitlesJson_AddsSubtitleTextTrack()
    {
        const string subtitlesJson = """[{"language":"nl","type":"full","ext":"vtt"}]""";
        Episode episode = BuildEpisodeWithVideoFile(subtitlesJson: subtitlesJson);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        VideoTrack track = dto.Tracks.Single(t => t.Kind == "subtitles");
        track.Language.Should().Be("nl");
        track.File.Should().EndWith(".nl.full.vtt");
    }

    [Fact]
    public void Ctor_Episode_EveryVariantOfALanguageKeepsItsOwnContainer()
    {
        // One file per format sits on disk — eng.full.ass, .sup, .vtt — and the
        // payload described them all as the same thing, because ext was used to
        // build the path and then dropped. A client defaults a missing ext to
        // vtt, so the menu showed "English (Full) VTT" three times over with no
        // way to tell them apart (Stoney: "they are all listed as vtt").
        const string subtitlesJson = """
            [
                {"language":"eng","type":"full","ext":"ass"},
                {"language":"eng","type":"full","ext":"sup"},
                {"language":"eng","type":"full","ext":"vtt"}
            ]
            """;
        Episode episode = BuildEpisodeWithVideoFile(subtitlesJson: subtitlesJson);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        List<VideoTrack> subtitles = dto.Tracks.Where(t => t.Kind == "subtitles").ToList();

        // The DISTINCTION, not the count: three rows that all say vtt is the bug.
        subtitles.Select(t => t.Ext).Should().BeEquivalentTo(["ass", "sup", "vtt"]);
    }

    [Fact]
    public void Ctor_Episode_MalformedSubtitlesJson_TreatedAsNoSubtitleTracks()
    {
        Episode episode = BuildEpisodeWithVideoFile(subtitlesJson: "{not-valid-json");

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Tracks.Should().NotContain(t => t.Kind == "subtitles");
    }

    [Fact]
    public void Ctor_Episode_LanguagesArrayContainsNullEntry_FiltersItOut()
    {
        Episode episode = BuildEpisodeWithVideoFile(languagesJson: "[\"en\", null]");

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Sources[0].Languages.Should().Equal("en");
    }

    [Fact]
    public void Ctor_Episode_LanguagesJsonIsNullLiteral_YieldsEmptyLanguagesArray()
    {
        Episode episode = BuildEpisodeWithVideoFile(languagesJson: "null");

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Sources[0].Languages.Should().BeEmpty();
    }

    [Fact]
    public void Ctor_Episode_TracksOrderedByLanguageAscending()
    {
        VideoTrack[] tracks =
        [
            new()
            {
                File = "/z.vtt",
                Kind = "captions",
                Language = "zzz",
            },
            new()
            {
                File = "/a.vtt",
                Kind = "captions",
                Language = "aaa",
            },
        ];
        Episode episode = BuildEpisodeWithVideoFile(tracks: tracks);

        VideoPlaylistResponseDto dto = new(episode, "tv", 1, "US");

        dto.Tracks.Select(t => t.Language).Should().BeInAscendingOrder();
    }
}
