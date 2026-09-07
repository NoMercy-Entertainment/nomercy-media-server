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

using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

public class CamelotKeyTests
{
    /// <summary>
    /// The detector can produce exactly these twenty-four names — twelve sharp
    /// pitch names, each with and without an "m" suffix — so the table is
    /// tested exhaustively rather than sampled.
    /// </summary>
    [Theory]
    [InlineData("C", "8B")]
    [InlineData("C#", "3B")]
    [InlineData("D", "10B")]
    [InlineData("D#", "5B")]
    [InlineData("E", "12B")]
    [InlineData("F", "7B")]
    [InlineData("F#", "2B")]
    [InlineData("G", "9B")]
    [InlineData("G#", "4B")]
    [InlineData("A", "11B")]
    [InlineData("A#", "6B")]
    [InlineData("B", "1B")]
    [InlineData("Cm", "5A")]
    [InlineData("C#m", "12A")]
    [InlineData("Dm", "7A")]
    [InlineData("D#m", "2A")]
    [InlineData("Em", "9A")]
    [InlineData("Fm", "4A")]
    [InlineData("F#m", "11A")]
    [InlineData("Gm", "6A")]
    [InlineData("G#m", "1A")]
    [InlineData("Am", "8A")]
    [InlineData("A#m", "3A")]
    [InlineData("Bm", "10A")]
    public void FromKeyName_MapsEveryKeyTheDetectorCanEmit(string keyName, string expected)
    {
        CamelotKey.FromKeyName(keyName).Should().Be(expected);
    }

    /// <summary>
    /// A relative major and minor pair share a pitch collection, so they share
    /// a Camelot number and differ only by letter. Getting this wrong is the
    /// mistake that silently mismatches tracks.
    /// </summary>
    [Theory]
    [InlineData("C", "Am", "8")]
    [InlineData("G", "Em", "9")]
    [InlineData("F", "Dm", "7")]
    [InlineData("B", "G#m", "1")]
    public void FromKeyName_GivesRelativePairsTheSameNumber(
        string major,
        string minor,
        string sharedNumber
    )
    {
        CamelotKey.FromKeyName(major).Should().Be(sharedNumber + "B");
        CamelotKey.FromKeyName(minor).Should().Be(sharedNumber + "A");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("H")]
    [InlineData("Db")]
    [InlineData("not a key")]
    public void FromKeyName_ReturnsNullRatherThanGuessing(string? keyName)
    {
        CamelotKey.FromKeyName(keyName).Should().BeNull();
    }

    [Fact]
    public void FromKeyName_TrimsSurroundingWhitespace()
    {
        CamelotKey.FromKeyName(" Am ").Should().Be("8A");
    }
}
