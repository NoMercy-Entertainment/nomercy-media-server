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

using NoMercy.MediaProcessing.Files.Parsing;

namespace NoMercy.Tests.MediaProcessing.Parsing;

/// <summary>
/// Locks the music album-path parser against the behaviour of the original
/// inlined regex across the supported library layouts (letter / [type] folders,
/// [year] and [Singles] releases, Windows and POSIX separators, and the
/// no-match folder-name fallback).
/// </summary>
public class MusicPathParserTests
{
    [Theory]
    [InlineData(["/data/Music/A/Adele/[2015] 25", 2015, "25", "Adele", null])]
    [InlineData([
        "/data/Music/[Albums]/Daft Punk/[2013] Random Access Memories",
        2013,
        "Random Access Memories",
        "Daft Punk",
        null,
    ])]
    [InlineData([
        @"D:\Music\R\Radiohead\[1997] OK Computer",
        1997,
        "OK Computer",
        "Radiohead",
        null,
    ])]
    [InlineData([
        "/m/Music/T/Taylor Swift/[Singles] Cardigan",
        0,
        "Cardigan",
        "Taylor Swift",
        "Singles",
    ])]
    [InlineData([
        "/srv/Music/M/Miles Davis/[1959] Kind of Blue",
        1959,
        "Kind of Blue",
        "Miles Davis",
        null,
    ])]
    public void Parses_album_layouts(
        string path,
        int year,
        string album,
        string? artist,
        string? releaseType
    )
    {
        MusicPathParser.MusicAlbumInfo info = MusicPathParser.Parse(path, "ignored");
        info.Year.Should().Be(year);
        info.AlbumName.Should().Be(album);
        info.Artist.Should().Be(artist);
        info.ReleaseType.Should().Be(releaseType);
    }

    [Theory]
    // unmatched path -> album falls back to the folder name (minus a [year] tag)
    [InlineData(["/m/Music/Adele", "Adele", "Adele"])]
    [InlineData(["not-a-library-path", "[2020] Best Of", "Best Of"])]
    [InlineData(["flat", "[1991] Nevermind", "Nevermind"])]
    public void Falls_back_to_folder_name(string path, string folder, string expectedAlbum)
    {
        MusicPathParser.MusicAlbumInfo info = MusicPathParser.Parse(path, folder);
        info.Year.Should().Be(0);
        info.AlbumName.Should().Be(expectedAlbum);
    }
}
