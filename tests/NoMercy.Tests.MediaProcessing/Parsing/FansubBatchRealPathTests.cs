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

using Moq;
using MovieFileLibrary;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.MediaProcessing.Files.Parsing.Adapters;
using NoMercy.MediaProcessing.Inbox;
using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Real download-folder paths, whole (batch folder and all), through the
/// production adapter set and every entry point that cuts a title from a name:
/// the resolver behind the library scan, the file manager and the add-content
/// locator, and the inbox classifier.
/// </summary>
public class FansubBatchRealPathTests
{
    private const string Shirase =
        "Download/complete/[GFD] Battle Programmer Shirase [BD 1080p x264 FLAC]/[GFD] Battle Programmer Shirase - 01 [BD 1080p x264 FLAC].mkv";

    private const string Kamiina =
        "Download/complete/[SubsPlease] Kamiina Botan, Yoeru Sugata wa Yuri no Hana (01-12) (1080p) [Batch]/[SubsPlease] Kamiina Botan, Yoeru Sugata wa Yuri no Hana - 01 (1080p) [E38EAFF4].mkv";

    private static FilenameParserPipeline ProductionPipeline() =>
        new([
            new EpisodePrefixAdapter(),
            new EpisodeWordAdapter(),
            new SeasonEpisodeAdapter(),
            new SeasonSpecialAdapter(),
            new CrossFormatAdapter(),
            new AnimeAbsoluteAdapter(),
            new EpisodeShortFormAdapter(),
            new SpecialsAdapter(),
            new SeasonPackAdapter(),
            new PartAdapter(),
            new MovieDetectorAdapter(),
        ]);

    public static TheoryData<string, string> Files =>
        new()
        {
            { Shirase, "Battle Programmer Shirase" },
            { Kamiina, "Kamiina Botan, Yoeru Sugata wa Yuri no Hana" },
        };

    [Theory]
    [MemberData(nameof(Files))]
    public void Resolver_CutsTheShowNameSeasonOneEpisodeOne(string path, string title)
    {
        foreach (string libraryType in new[] { "anime", "tv" })
        {
            MovieFile parsed = new FilenameResolver(ProductionPipeline())
                .Resolve(Path.GetFileName(path), Path.GetDirectoryName(path), path, libraryType)
                .Parsed;

            parsed.Title.Should().Be(title, $"library type {libraryType}");
            parsed.Season.Should().Be(1, $"library type {libraryType}");
            parsed.Episode.Should().Be(1, $"library type {libraryType}");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public async Task Inbox_SearchesTheShowNameOnly(string path, string title)
    {
        Mock<IInboxMetadataProbe> probe = new();
        probe
            .Setup(p =>
                p.SearchMoviesAsync(
                    It.IsAny<string>(),
                    It.IsAny<int?>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([]);
        probe
            .Setup(p =>
                p.SearchTvAsync(It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([]);
        Mock<IMediaTypeClassifier> mediaType = new();
        mediaType
            .Setup(c => c.ClassifyAsync(It.IsAny<string>(), It.IsAny<int?>()))
            .ReturnsAsync("anime");

        InboxClassifier classifier = new(
            probe.Object,
            new Mock<IInboxAudioTagReader>().Object,
            mediaType.Object,
            ProductionPipeline()
        );
        ClassificationResult result = await classifier.Classify(
            path,
            Ulid.NewUlid(),
            CancellationToken.None
        );

        probe.Verify(
            p => p.SearchTvAsync(title, It.IsAny<int?>(), It.IsAny<CancellationToken>()),
            Times.Once
        );
        result.DetectedType.Should().NotBe("unknown");
    }
}
