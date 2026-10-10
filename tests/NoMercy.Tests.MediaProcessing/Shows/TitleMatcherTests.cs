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

using NoMercy.MediaProcessing.Shows;

namespace NoMercy.Tests.MediaProcessing.Shows;

public class TitleMatcherTests
{
    [Fact]
    public void Matches_WordOrderReordered_StillMatches()
    {
        // Reproduced against AniList: "SAINT SEIYA: Knights of the Zodiac"
        // (search) vs "Knights of the Zodiac: Saint Seiya" (candidate).
        bool result = TitleMatcher.Matches(
            "SAINT SEIYA: Knights of the Zodiac",
            ["Knights of the Zodiac: Saint Seiya"]
        );

        result.Should().BeTrue();
    }

    [Fact]
    public void Matches_CandidateHasYearSuffix_StillMatches()
    {
        bool result = TitleMatcher.Matches("Fruits Basket", ["Fruits Basket (2019)"]);

        result.Should().BeTrue();
    }

    [Fact]
    public void Matches_CurlyApostropheInCandidate_StillMatches()
    {
        bool result = TitleMatcher.Matches("Journey's End", ["Journey’s End"]);

        result.Should().BeTrue();
    }

    [Fact]
    public void Matches_HyphenatedLocalTitleWithLeadingDash_StillMatches()
    {
        // Re:ZERO -Starting Life in Another World- : the dash must be
        // normalized to a space, not treated as an exclusion token.
        bool result = TitleMatcher.Matches(
            "Re:ZERO -Starting Life in Another World-",
            ["Re:ZERO Starting Life in Another World"]
        );

        result.Should().BeTrue();
    }

    [Fact]
    public void Matches_AniListSynonymsArrayIncluded_StillMatches()
    {
        // AniList's candidate set is Title.Romaji/English/Native + Synonyms;
        // a synonym-only match must still succeed.
        bool result = TitleMatcher.Matches("OP", [null, "One Piece", null, "OP"]);

        result.Should().BeTrue();
    }

    [Fact]
    public void Matches_NoOverlap_ReturnsFalse()
    {
        bool result = TitleMatcher.Matches("Attack on Titan", ["One Piece"]);

        result.Should().BeFalse();
    }

    [Fact]
    public void Matches_EmptySearchTitle_ReturnsFalse()
    {
        bool result = TitleMatcher.Matches("", ["One Piece"]);

        result.Should().BeFalse();
    }

    /// <summary>
    /// Every case below is a real show that sat in the Series library because the
    /// matcher rejected the correct AniList hit over a separator character. TMDB
    /// and AniList disagree on en dashes, ampersand-vs-and, and the decorative
    /// star, tilde and gender marks that anime titles carry.
    /// </summary>
    [Theory]
    [InlineData(
        "KONOSUBA – An Explosion on This Wonderful World!",
        "KONOSUBA -An Explosion on This Wonderful World!"
    )]
    [InlineData(
        "Berserk: The Golden Age Arc – Memorial Edition",
        "Berserk: The Golden Age Arc - Memorial Edition"
    )]
    [InlineData("Level 1 Demon Lord & One Room Hero", "Level 1 Demon Lord and One Room Hero")]
    [InlineData("Saint Cecilia and Pastor Lawrence", "Saint Cecilia & Pastor Lawrence")]
    [InlineData("Please Twins!", "Please☆Twins!")]
    [InlineData("Rin: Daughters of Mnemosyne", "RIN ~Daughters of Mnemosyne~")]
    [InlineData(
        "Reborn to Master the Blade: From Hero-King to Extraordinary Squire ♀",
        "Reborn to Master the Blade: From Hero-King to Extraordinary Squire"
    )]
    public void Matches_SeparatorAndDecorationDifferences_StillMatch(
        string searchTitle,
        string candidateTitle
    )
    {
        TitleMatcher.Matches(searchTitle, [candidateTitle]).Should().BeTrue();
    }

    /// <summary>
    /// Normalising separators must not collapse genuinely different titles: a
    /// K-pop music show that is not anime still has to fail against the unrelated
    /// anime AniList returns for it.
    /// </summary>
    [Fact]
    public void Matches_UnrelatedTitle_StillDoesNotMatch()
    {
        TitleMatcher
            .Matches("&TEAM EPISODE", ["Tenchi Souzou Design-bu: Tokubetsu-hen"])
            .Should()
            .BeFalse();
    }

    /// <summary>
    /// TMDB writes "The Piano Forest"; AniList lists it as "Piano Forest". The
    /// leading article was the only word that failed, so a real anime stayed
    /// filed under the tv library.
    /// </summary>
    [Fact]
    public void Matches_LeadingArticleOnlyOnTheSearchTitle_StillMatches()
    {
        TitleMatcher.Matches("The Piano Forest", ["Piano Forest"]).Should().BeTrue();
    }
}
