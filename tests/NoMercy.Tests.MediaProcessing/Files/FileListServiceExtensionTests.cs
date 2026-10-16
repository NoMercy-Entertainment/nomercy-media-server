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
using NoMercy.MediaProcessing.Files;
using NoMercy.MediaProcessing.Files.Parsing;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Files;

/// <summary>
/// Files copied from Windows, a camera or a ripper are often named Movie.MKV
/// or Track.MP3. A case-sensitive check skipped them without a word, and the
/// library simply looked incomplete (#245).
/// </summary>
[Trait("Category", "Unit")]
public class FileListServiceExtensionTests
{
    private static readonly string[] Folder =
    [
        "Movies/Upper.MKV",
        "Movies/lower.mkv",
        "Movies/Mixed.Mp4",
        "Movies/Old.AVI",
        "Music/Song.MP3",
        "Music/Lossless.Flac",
        "Music/Voice.WAV",
        "Movies/notes.TXT",
        "Movies/cover.JPG",
    ];

    [Fact]
    public void A_video_file_is_listed_whatever_the_case_of_its_extension()
    {
        string[] found = [.. Service().GetVideoFilesInDirectory("root").Select(file => file.Name)];

        found.Should().BeEquivalentTo("Upper.MKV", "lower.mkv", "Mixed.Mp4", "Old.AVI");
    }

    [Fact]
    public void An_audio_file_is_listed_whatever_the_case_of_its_extension()
    {
        string[] found = [.. Service().GetAudioFilesInDirectory("root").Select(file => file.Name)];

        found.Should().BeEquivalentTo("Song.MP3", "Lossless.Flac", "Voice.WAV");
    }

    private static FileListService Service()
    {
        Mock<IStorageDriver> driver = new();
        driver
            .Setup(d =>
                d.EnumerateFileSystemEntries(
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<SearchOption>()
                )
            )
            .Returns(Folder);
        driver.Setup(d => d.DirectoryExists(It.IsAny<string>())).Returns(false);

        return new(
            driver.Object,
            new Mock<IMediaIdentificationService>().Object,
            new FilenameParserPipeline([]),
            NullLogger<FileListService>.Instance
        );
    }
}
