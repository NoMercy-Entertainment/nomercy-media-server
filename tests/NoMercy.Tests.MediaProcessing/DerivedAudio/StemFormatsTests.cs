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

using FluentAssertions;
using NoMercy.MediaProcessing.DerivedAudio;

namespace NoMercy.Tests.MediaProcessing.DerivedAudio;

/// <summary>
/// The one table saying which stem format may be stored under which content
/// type. A register row that claims Opus over a FLAC file is a player error
/// hours later, so the pairing is checked rather than assumed - and checked in
/// one place, because two copies of it drift.
/// </summary>
[Trait("Category", "Unit")]
public class StemFormatsTests
{
    [Theory]
    [InlineData("opus", "audio/ogg", true)]
    [InlineData("opus", "audio/opus", true)]
    [InlineData("flac", "audio/flac", true)]
    [InlineData("opus", "audio/flac", false)]
    [InlineData("flac", "audio/ogg", false)]
    [InlineData("flac", "audio/opus", false)]
    [InlineData("mp3", "audio/mpeg", false)]
    [InlineData("opus", "audio/mpeg", false)]
    [InlineData("", "audio/ogg", false)]
    public void Matches_AnswersThePairingTable(string format, string contentType, bool expected) =>
        StemFormats.Matches(format, contentType).Should().Be(expected);

    /// <summary>
    /// Case is not what a plugin is refused over: a third-party producer that
    /// writes "OPUS" and "AUDIO/OGG" means the same pair.
    /// </summary>
    [Theory]
    [InlineData("OPUS", "audio/ogg")]
    [InlineData("opus", "AUDIO/OGG")]
    [InlineData("Flac", "Audio/Flac")]
    public void Matches_IgnoresCase(string format, string contentType) =>
        StemFormats.Matches(format, contentType).Should().BeTrue();

    [Fact]
    public void TheVocabularyIsTheOneTheStoreWrites()
    {
        StemFormats.Opus.Should().Be("opus");
        StemFormats.Flac.Should().Be("flac");
        StemFormats.OggContentType.Should().Be("audio/ogg");
        StemFormats.OpusContentType.Should().Be("audio/opus");
        StemFormats.FlacContentType.Should().Be("audio/flac");
    }
}
