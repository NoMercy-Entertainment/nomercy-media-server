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

using System.Text;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Tests.NmSystem;

/// <summary>
/// A name written on one host must be readable on a shared drive from Windows, Linux and
/// macOS: no reserved character, no trailing dot or space, no reserved device name, and a
/// length every file system accepts.
/// </summary>
[Trait("Category", "Unit")]
public class PortableFileNameTests
{
    [Theory]
    [InlineData("[2018] ?", "[2018] _")]
    [InlineData("AC/DC", "AC_DC")]
    [InlineData("Title: Part 2", "Title_ Part 2")]
    [InlineData("a*b\"c<d>e|f\\g", "a_b_c_d_e_f_g")]
    [InlineData("tab\there\u0001", "tab_here_")]
    [InlineData("Name.", "Name_")]
    [InlineData("Name...", "Name_")]
    [InlineData("Name ", "Name_")]
    [InlineData("..", "_")]
    [InlineData("", "_")]
    [InlineData("Plain Name.flac", "Plain Name.flac")]
    [InlineData("[2018] Love (Deluxe)", "[2018] Love (Deluxe)")]
    public void A_segment_loses_every_character_a_shared_drive_rejects(string name, string expected)
    {
        PortableFileName.Segment(name).Should().Be(expected);
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("con", "con_")]
    [InlineData("PRN", "PRN_")]
    [InlineData("AUX", "AUX_")]
    [InlineData("NUL", "NUL_")]
    [InlineData("COM1", "COM1_")]
    [InlineData("COM9", "COM9_")]
    [InlineData("LPT1", "LPT1_")]
    [InlineData("LPT9", "LPT9_")]
    [InlineData("CON.flac", "CON_.flac")]
    [InlineData("nul.txt", "nul_.txt")]
    [InlineData("Con.Air.(1997)", "Con.Air.(1997)")]
    [InlineData("COM10", "COM10")]
    [InlineData("Console", "Console")]
    [InlineData("CONTROL.flac", "CONTROL.flac")]
    public void A_reserved_device_name_is_made_safe_with_or_without_an_extension(
        string name,
        string expected
    )
    {
        PortableFileName.Segment(name).Should().Be(expected);
    }

    [Fact]
    public void A_long_segment_is_cut_to_255_bytes_and_keeps_its_extension()
    {
        string name = new string('日', 120) + ".flac";

        string segment = PortableFileName.Segment(name);

        Encoding.UTF8.GetByteCount(segment).Should().BeLessThanOrEqualTo(255);
        segment.Should().EndWith(".flac");
        segment.Should().StartWith("日日");
    }

    [Fact]
    public void A_segment_within_the_limit_is_left_alone()
    {
        string name = new string('a', 250) + ".mp3";

        PortableFileName.Segment(name).Should().Be(name);
    }

    [Fact]
    public void A_linux_path_is_sanitized_segment_by_segment_and_keeps_its_separators()
    {
        PortableFileName
            .Path("/mnt/music/B/Buble, Michael/[2018] ?/CON.flac")
            .Should()
            .Be("/mnt/music/B/Buble, Michael/[2018] _/CON_.flac");
    }

    [Theory]
    [InlineData("[2018] ❤", "[2018] ❤")]
    [InlineData("\U0001F3B5 Party \U0001F389", "\U0001F3B5 Party \U0001F389")]
    [InlineData("Café del Mar", "Café del Mar")]
    [InlineData("日本語", "日本語")]
    public void A_segment_keeps_every_real_unicode_character(string name, string expected)
    {
        PortableFileName.Segment(name).Should().Be(expected);
    }

    [Fact]
    public void A_long_emoji_name_is_cut_between_characters_never_inside_one()
    {
        string name = string.Concat(Enumerable.Repeat("\U0001F3B5", 100));

        string result = PortableFileName.Segment(name);

        Encoding.UTF8.GetByteCount(result).Should().BeLessThanOrEqualTo(255);
        result.Should().MatchRegex("^(\U0001F3B5)+$");
    }

    [Fact]
    public void A_path_segment_that_climbs_out_is_neutralized()
    {
        PortableFileName.Path("A/../B").Should().Be("A/_/B");
    }

    [Theory]
    [InlineData("CON", "CON_")]
    [InlineData("Name.", "Name")]
    [InlineData("Title: Part 2", "Title.Part.2")]
    [InlineData("AC/DC", "AC.DC")]
    [InlineData("[2018] ?", "[2018]")]
    [InlineData("Movie.(1999)", "Movie.(1999)")]
    public void The_video_name_cleaner_goes_through_the_same_guard(string name, string expected)
    {
        name.CleanFileName().Should().Be(expected);
    }

    [Fact]
    public void The_video_name_cleaner_drops_control_characters_on_every_host()
    {
        "Show\u0001Name".CleanFileName().Should().Be("Show.Name");
    }
}
