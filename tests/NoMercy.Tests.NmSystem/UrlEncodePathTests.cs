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

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class UrlEncodePathTests
{
    [Theory]
    [InlineData(
        "/Tengoku.S01E06.100%.Safe.Water.NoMercy.m3u8",
        "/Tengoku.S01E06.100%25.Safe.Water.NoMercy.m3u8"
    )]
    [InlineData(
        "/Designated.Survivor.S03E01.#thesystemisbroken.mp4",
        "/Designated.Survivor.S03E01.%23thesystemisbroken.mp4"
    )]
    [InlineData("/Who.Cares?.mp4", "/Who.Cares%3F.mp4")]
    [InlineData("/Show.[1080p]/file.mkv", "/Show.%5B1080p%5D/file.mkv")]
    [InlineData("/Show.{Extended}/file.mkv", "/Show.%7BExtended%7D/file.mkv")]
    [InlineData("/A Space/file.mkv", "/A%20Space/file.mkv")]
    public void EncodePath_EscapesCharactersThatBreakUrlParsing(string path, string expected)
    {
        string result = path.EncodePath();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("/01KXXNHX/Tengoku.Daimakyo.(2023)/Tengoku.Daimakyo.S01E06.NoMercy.m3u8")]
    [InlineData("/share/Movie.Title.-.Director's.Cut!.NoMercy.mp4")]
    [InlineData("/share/Fast.&.Furious,.Part.2.mp4")]
    [InlineData("/share/anime/進撃の巨人.S01E01.mkv")]
    public void EncodePath_LeavesLegalPathCharactersByteIdentical(string path)
    {
        string result = path.EncodePath();
        result.Should().Be(path);
    }

    [Fact]
    public void EncodePath_KeepsSlashesAsSeparators()
    {
        string result = "/a/b/c.m3u8".EncodePath();
        result.Should().Be("/a/b/c.m3u8");
    }

    [Fact]
    public void EncodedPath_RoundTripsBackToTheOriginalFilename()
    {
        const string path = "/share/Show/Ep.100%.Safe.#1.[x265].m3u8";

        string decoded = Uri.UnescapeDataString(path.EncodePath());

        decoded.Should().Be(path);
    }

    [Fact]
    public void EncodedPath_ParsesAsASingleUriWithNoFragment()
    {
        const string path = "/share/Show/Ep.#thesystemisbroken.mp4";

        Uri uri = new(new("https://server.nomercy.tv"), path.EncodePath());

        uri.Fragment.Should().BeEmpty();
        uri.AbsolutePath.Should().Be("/share/Show/Ep.%23thesystemisbroken.mp4");
    }
}
