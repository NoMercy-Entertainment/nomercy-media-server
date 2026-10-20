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

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using NoMercy.OpticalMedia.Metadata;

namespace NoMercy.Tests.OpticalMedia.Metadata;

[Trait("Category", "Unit")]
public class DiscIdAlgorithmTests
{
    // ── MusicBrainzDiscId.Compute — algorithm correctness ─────────────────

    /// <summary>
    /// Canonical worked example from the MusicBrainz Disc ID Calculation spec
    /// (https://musicbrainz.org/doc/Disc_ID_Calculation): a 6-track disc whose
    /// absolute frame offsets and lead-out produce a known disc id. Offsets are
    /// absolute (track 1 = 150), exactly as the algorithm hashes them. This is
    /// the only test that proves the hash is byte-correct — a wrong pre-gap or
    /// hex width would change the result.
    /// </summary>
    [Fact]
    public void Compute_CanonicalSpecFixture_ReturnsExpectedDiscId()
    {
        DiscToc toc = new(
            FirstTrack: 1,
            LastTrack: 6,
            LeadOutOffsetSectors: 95462,
            TrackOffsetsSectors: [150, 15363, 32314, 46592, 63414, 80489]
        );

        string discId = MusicBrainzDiscId.Compute(toc);

        discId.Should().Be("49HHV7Eb8UKF3aQiNmu1GR8vKTY-");
    }

    [Fact]
    public void DocumentedAlgorithm_CanonicalSpecFixture_ReproducesThePublishedDiscId()
    {
        string discId = DocumentedDiscId(
            firstTrack: 1,
            lastTrack: 6,
            leadOutOffsetSectors: 95462,
            offsetByTrackNumber: new Dictionary<int, int>
            {
                [1] = 150,
                [2] = 15363,
                [3] = 32314,
                [4] = 46592,
                [5] = 63414,
                [6] = 80489,
            }
        );

        discId
            .Should()
            .Be(
                "49HHV7Eb8UKF3aQiNmu1GR8vKTY-",
                "the reference the FirstTrack tests compare against must itself match the published example"
            );
    }

    [Fact]
    public void Compute_FirstTrackAboveOne_PutsEachOffsetInItsTrackNumberSlot()
    {
        // The array is laid out from FirstTrack (index 0 is track 2 here); the
        // hash input is laid out by track number, so track 1's slot stays 0.
        DiscToc toc = new(
            FirstTrack: 2,
            LastTrack: 3,
            LeadOutOffsetSectors: 40000,
            TrackOffsetsSectors: [150, 20150]
        );

        string discId = MusicBrainzDiscId.Compute(toc);

        discId
            .Should()
            .Be(DocumentedDiscId(2, 3, 40000, new Dictionary<int, int> { [2] = 150, [3] = 20150 }));
        discId
            .Should()
            .NotBe(
                DocumentedDiscId(2, 3, 40000, new Dictionary<int, int> { [1] = 150, [2] = 20150 }),
                "the offsets must not be packed from track 1's slot as if the disc started at track 1"
            );
    }

    [Fact]
    public void Compute_AlwaysProduces28CharSubstitutedId()
    {
        DiscToc toc = new(
            FirstTrack: 1,
            LastTrack: 6,
            LeadOutOffsetSectors: 95462,
            TrackOffsetsSectors: [150, 15363, 32314, 46592, 63414, 80489]
        );

        string discId = MusicBrainzDiscId.Compute(toc);

        discId.Should().HaveLength(28, "MusicBrainz disc IDs are always 28 chars");
        discId.Should().MatchRegex(@"^[A-Za-z0-9._-]+$", "base64url-like alphabet only");
        discId.Should().NotContainAny("+", "/", "=");
    }

    [Fact]
    public void Compute_SameToc_IsDeterministic()
    {
        DiscToc toc = new(
            FirstTrack: 1,
            LastTrack: 3,
            LeadOutOffsetSectors: 60150,
            TrackOffsetsSectors: [150, 15150, 30150]
        );

        MusicBrainzDiscId.Compute(toc).Should().Be(MusicBrainzDiscId.Compute(toc));
    }

    [Fact]
    public void Compute_DifferentLeadOut_ProducesDifferentId()
    {
        DiscToc tocA = new(1, 1, 18150, [150]);
        DiscToc tocB = new(1, 1, 19150, [150]);

        MusicBrainzDiscId.Compute(tocA).Should().NotBe(MusicBrainzDiscId.Compute(tocB));
    }

    [Fact]
    public void Compute_MismatchedTrackCount_Throws()
    {
        DiscToc toc = new(
            FirstTrack: 1,
            LastTrack: 3,
            LeadOutOffsetSectors: 60150,
            TrackOffsetsSectors: [150, 15150]
        );

        Action act = () => MusicBrainzDiscId.Compute(toc);
        act.Should().Throw<ArgumentException>();
    }

    // ── NullTocReader ─────────────────────────────────────────────────────

    [Fact]
    public async Task NullTocReader_AlwaysReturnsNull()
    {
        NullTocReader reader = new();
        DiscToc? result = await reader.ReadTocAsync("/dev/sr0", CancellationToken.None);
        result.Should().BeNull();
    }

    /// <summary>
    /// The published algorithm (https://musicbrainz.org/doc/Disc_ID_Calculation,
    /// libdiscid disc.c): first and last track number as two upper-case hex
    /// digits, then 100 offsets of eight hex digits — the lead-out followed by
    /// one slot per track number 1..99 holding that track's absolute offset, or
    /// 0 when the disc has no such track — SHA-1 hashed and base64 encoded with
    /// <c>+ / =</c> replaced by <c>. _ -</c>. Keyed by track number so it does
    /// not depend on how <see cref="DiscToc"/> lays out its offset array.
    /// </summary>
    private static string DocumentedDiscId(
        int firstTrack,
        int lastTrack,
        int leadOutOffsetSectors,
        IReadOnlyDictionary<int, int> offsetByTrackNumber
    )
    {
        StringBuilder hex = new();
        hex.Append(firstTrack.ToString("X2", CultureInfo.InvariantCulture));
        hex.Append(lastTrack.ToString("X2", CultureInfo.InvariantCulture));
        hex.Append(leadOutOffsetSectors.ToString("X8", CultureInfo.InvariantCulture));
        for (int trackNumber = 1; trackNumber <= 99; trackNumber++)
        {
            int offset = offsetByTrackNumber.GetValueOrDefault(trackNumber, 0);
            hex.Append(offset.ToString("X8", CultureInfo.InvariantCulture));
        }

        byte[] digest = SHA1.HashData(Encoding.ASCII.GetBytes(hex.ToString()));
        return Convert.ToBase64String(digest).Replace('+', '.').Replace('/', '_').Replace('=', '-');
    }
}
