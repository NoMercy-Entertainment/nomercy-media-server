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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using MovieFileLibrary;
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Files.Parsing.Adapters;
using NoMercy.Providers.TMDB.Models.Shared;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// The title handed to identification is the search term, so anything the
/// filename parser leaves behind in it is searched for verbatim.
///
/// Release groups that name episodes '&lt;Show&gt; - &lt;absolute&gt; (SxxEyy) (...)' split the
/// title at the season marker, which leaves both the absolute number and the opening
/// bracket of the marker glued to the show name. Searching that finds nothing, and a
/// whole season comes back unidentified.
/// </summary>
public class FileListServiceTitleCleanupTests
{
    private static async Task<List<MovieFile>> ParsedTitlesFor(
        string libraryType,
        params string[] fileNames
    )
    {
        Mock<IStorageDriver> driver = new();
        Mock<IStorage> storage = new();
        storage.SetupGet(s => s.Driver).Returns(driver.Object);

        List<StorageEntry> entries = fileNames
            .Select(name => new StorageEntry($"Anime/{name}", false, 1000, DateTimeOffset.UtcNow))
            .ToList();
        storage
            .Setup(s => s.List(It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<bool>()))
            .Returns(entries);

        List<MovieFile> seen = [];
        Mock<IMediaIdentificationService> identification = new();
        identification
            .Setup(i =>
                i.IdentifyAsync(
                    It.IsAny<MovieFile>(),
                    It.IsAny<string>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<int?>(),
                    It.IsAny<bool>(),
                    It.IsAny<DateOnly?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .Callback(
                (
                    MovieFile parsed,
                    string _,
                    TimeSpan? _,
                    int? _,
                    bool _,
                    DateOnly? _,
                    CancellationToken _
                ) =>
                {
                    lock (seen)
                        seen.Add(parsed);
                }
            )
            .ReturnsAsync(((MovieOrEpisode match, string? imdbId)?)null);

        FileListService service = new(
            driver.Object,
            identification.Object,
            new FilenameParserPipeline(
                new IFilenameParseAdapter[]
                {
                    new EpisodePrefixAdapter(),
                    new EpisodeWordAdapter(),
                    new SeasonEpisodeAdapter(),
                    new MovieDetectorAdapter(),
                }
            ),
            NullLogger<FileListService>.Instance
        );

        await service.GetFilesInDirectory("Anime", libraryType, storage.Object);
        return seen;
    }

    /// <summary>
    /// The absolute number is the one that resolves. Providers commonly model a long-running
    /// show as a single season, so the release's "S02E01" has no row to match while absolute
    /// 29 does — taking the marker instead lands on season 1 episode 1 and imports a
    /// completely different episode.
    /// </summary>
    [Fact]
    public async Task AbsoluteNumberedRelease_KeepsTheAbsoluteEpisode_NotTheMarker()
    {
        List<MovieFile> parsed = await ParsedTitlesFor(
            "anime",
            "[9volt] Sousou no Frieren - 29 (S02E01) (Dual Audio) (WEB 1080p HEVC EAC-3) [E15A4F27].mkv"
        );

        parsed.Should().ContainSingle();
        parsed[0].Title.Should().Be("Sousou no Frieren");
        parsed[0].Episode.Should().Be(29);
    }

    /// <summary>
    /// Without a competing absolute number the marker is all there is, so it must still be
    /// read through a bracket — that is how these releases write it.
    /// </summary>
    [Fact]
    public async Task BracketedMarkerAlone_IsReadAsSeasonAndEpisode()
    {
        List<MovieFile> parsed = await ParsedTitlesFor(
            "anime",
            "[Judas] Some Show (S03E07) [1080p][HEVC].mkv"
        );

        parsed.Should().ContainSingle();
        parsed[0].Season.Should().Be(3);
        parsed[0].Episode.Should().Be(7);
    }

    [Theory]
    [InlineData("[SubsPlease] Show Name - 12 (1080p) [ABCD1234].mkv", "Show Name")]
    [InlineData("[Judas] Blue Lock - 05 (S01E05) [1080p][HEVC].mkv", "Blue Lock")]
    [InlineData("[Erai-raws] Another Show - 100 (S03E04) [1080p].mkv", "Another Show")]
    public async Task ReleaseGroupNaming_LeavesOnlyTheShowName(string fileName, string expected)
    {
        List<MovieFile> parsed = await ParsedTitlesFor("anime", fileName);

        parsed.Should().ContainSingle();
        parsed[0].Title.Should().Be(expected);
    }

    /// <summary>
    /// A show whose name genuinely ends in a number must survive the cleanup, or fixing
    /// the release-group case would break every title like this one.
    /// </summary>
    [Theory]
    [InlineData("Mobile Suit Gundam 00 - S01E03 [1080p].mkv", "Mobile Suit Gundam 00")]
    [InlineData("Psycho-Pass 2 - S01E02.mkv", "Psycho-Pass 2")]
    public async Task TitleEndingInANumber_IsNotTruncated(string fileName, string expected)
    {
        List<MovieFile> parsed = await ParsedTitlesFor("anime", fileName);

        parsed.Should().ContainSingle();
        parsed[0].Title.Should().Be(expected);
    }
}
