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

using System.Buffers.Binary;
using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Tests.Encoder.ContentAnalysis.Segments.Support;

namespace NoMercy.Tests.Encoder.ContentAnalysis.Segments;

/// <summary>
/// The raw chromaprint output and its timing, on real muxer bytes (#253, #345).
/// </summary>
[Trait("Category", "Unit")]
public class ChromaprintRawTests
{
    // Real "-fp_format raw" muxer output for a 6 s synthetic clip (27 hashes),
    // the same capture ChromaprintFingerprinterTests pins the base64 decoder to.
    private static readonly uint[] RealRawHashes =
    [
        3665379362u,
        3660120098u,
        3727360034u,
        3710517282u,
        1412038694u,
        1961507894u,
        816454414u,
        279583308u,
        279579276u,
        27990668u,
        36190860u,
        103298716u,
        103309740u,
        103309736u,
        372793769u,
        910975403u,
        390427050u,
        356744490u,
        97777962u,
        91351342u,
        90237246u,
        1298508046u,
        1307421966u,
        1303224586u,
        3384643864u,
        3650728248u,
        3650855208u,
    ];

    [Fact]
    public void Parse_RealMuxerBytes_ReadsLittleEndianUInt32()
    {
        byte[] raw = new byte[RealRawHashes.Length * 4];
        for (int i = 0; i < RealRawHashes.Length; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(raw.AsSpan(i * 4), RealRawHashes[i]);

        ChromaprintRaw.Parse(raw).Should().Equal(RealRawHashes);
    }

    [Fact]
    public void Parse_BytesThatLookLikeText_AreStillHashes()
    {
        // #253: the old parser scanned for digits and commas and cut at '='.
        // "2,=9" as bytes is one hash, not the numbers 2 and 9.
        byte[] raw = "2,=9"u8.ToArray();

        ChromaprintRaw.Parse(raw).Should().Equal([0x393D2C32u]);
    }

    [Fact]
    public void Parse_TrailingPartialHash_IsDropped()
    {
        ChromaprintRaw.Parse([1, 0, 0, 0, 7, 7]).Should().Equal([1u]);
        ChromaprintRaw.Parse([]).Should().BeEmpty();
    }

    [Fact]
    public void Parse_HellsingE01_WholeTrack_Is11126Hashes()
    {
        // The issue's measurement: 11126 hashes for the 1380 s episode.
        string fp = Path.Combine(
            ResearchCorpus.CommittedRoot,
            "hellsing-s01",
            "Hellsing.S01E01",
            "fp_full.raw"
        );

        uint[] hashes = ChromaprintRaw.Parse(File.ReadAllBytes(fp));

        hashes.Should().HaveCount(11126);
        hashes
            .Distinct()
            .Count()
            .Should()
            .BeGreaterThan(5000, "real hashes, not stray ASCII digits");
    }

    [Fact]
    public void SecondsPerHash_IsTheChromaprintHopOf1365Samples()
    {
        ChromaprintRaw.SecondsPerHash.Should().BeApproximately(0.1238095, 1e-7);
        ChromaprintRaw.HashToSeconds(100).Should().BeApproximately(12.381, 0.001);
    }

    [Fact]
    public void HashTimes_OfRealWholeTrackPrints_LieInsideTheirEpisodes()
    {
        // #345: chromaprint needs a fixed ~2.7 s of audio before its first hash, so
        // the last hash sits that much before the end. With the old 2048-sample
        // hop (0.1858 s) the last hash of a 23 min episode landed at 34 min.
        // (The hop itself is fitted over 81 episodes of every length in
        // ResearchCorpusTests, which needs the research folder.)
        foreach (RecordedEpisode episode in ResearchCorpus.Committed("hellsing-s01").Episodes)
        {
            int hashes = episode.Signals.Fingerprint.Single().Hashes.Length;
            double lead = episode.Signals.DurationSeconds - ChromaprintRaw.HashToSeconds(hashes);

            lead.Should().BeInRange(2.0, 3.5, episode.Name);
            (episode.Signals.DurationSeconds - hashes * 2048.0 / 11025).Should().BeNegative();
        }
    }
}
