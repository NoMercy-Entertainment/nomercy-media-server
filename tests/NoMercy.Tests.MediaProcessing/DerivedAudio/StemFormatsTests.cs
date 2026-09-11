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

    /// <summary>
    /// A format the caller never filled in is not a pairing, and the
    /// members-present check in front of this one is what names it in words.
    /// Answering false leaves that refusal where it belongs instead of
    /// throwing past it.
    /// </summary>
    [Fact]
    public void Matches_ANullFormat_IsFalse() =>
        StemFormats.Matches(null, StemFormats.OggContentType).Should().BeFalse();

    /// <summary>
    /// The other side of the pair is not a caller's to get wrong: the content
    /// type is read back out of a <c>DerivedAudio</c> row the server wrote
    /// itself, so a row without one is a data error in the register. It is
    /// thrown rather than answered false, because "false" would reach a plugin
    /// as an ordinary format mismatch and send the owner after the stem.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Matches_ARegisterRowWithoutAContentType_Throws(string? contentType)
    {
        Func<bool> matching = () => StemFormats.Matches(StemFormats.Opus, contentType);

        matching
            .Should()
            .Throw<InvalidOperationException>()
            .WithMessage("a DerivedAudio row has no content type");
    }

    /// <summary>
    /// One casing in the register, whatever a producer wrote: two rows that
    /// differ only in case are two rows to every query that does not know to
    /// ask twice.
    /// </summary>
    [Theory]
    [InlineData("OPUS", "opus")]
    [InlineData("Flac", "flac")]
    [InlineData("Vocals", "vocals")]
    [InlineData("accompaniment", "accompaniment")]
    // Not a throw: the members-present check is what names a missing token,
    // and the batch-shape checks that canonicalize one run before it.
    [InlineData(null, "")]
    public void Canonical_LowerCasesTheToken(string? written, string stored) =>
        StemFormats.Canonical(written).Should().Be(stored);

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
