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

/// <summary>
/// Pins the pure string helpers in <see cref="Str"/> — fuzzy matching, filename
/// metadata extraction, sanitization, case conversion and numeric parsing — that
/// the scanner and metadata pipeline lean on. No DB, network or filesystem.
/// </summary>
[Trait("Category", "Unit")]
public class StrTests
{
    [Theory]
    [InlineData(["hello", "hello", 100.0])]
    [InlineData(["abc", "abd", 200.0 / 3.0])]
    [InlineData(["", "anything", 0.0])]
    [InlineData(["anything", "", 0.0])]
    public void MatchPercentage_ScoresSimilarity(string a, string b, double expected)
    {
        FuzzyMatcher.MatchPercentage(a, b).Should().BeApproximately(expected, 0.001);
    }

    [Fact]
    public void MatchPercentage_IsCaseInsensitive()
    {
        FuzzyMatcher.MatchPercentage("Hello", "hello").Should().Be(100.0);
    }

    [Theory]
    [InlineData(["Movie.2009.1080p.mkv", "2009"])]
    [InlineData(["Some Film (2021)", "2021"])]
    [InlineData(["Western 1899", "1899"])]
    [InlineData(["1080p", null])]
    [InlineData(["no year here", null])]
    public void TryGetYear_ExtractsFourDigitYear(string input, string? expected)
    {
        input.TryGetYear().Should().Be(expected);
    }

    [Theory]
    [InlineData(["Movie [tmdb-1234] 1080p.mkv", 1234])]
    [InlineData(["Show [TMDB-99].mkv", 99])]
    [InlineData(["No hint here.mkv", null])]
    public void TryGetTmdbHint_ExtractsId(string input, int? expected)
    {
        input.TryGetTmdbHint().Should().Be(expected);
    }

    [Theory]
    [InlineData(["A & B!", "aandb"])]
    [InlineData(["The Matrix", "thematrix"])]
    [InlineData(["café", "café"])] // non-ASCII letters kept, not stripped (#379)
    public void NormalizeForComparison_StripsToLowerAlphaNumeric(string input, string expected)
    {
        input.NormalizeForComparison().Should().Be(expected);
    }

    [Theory]
    [InlineData(["1:02:03", 3723])]
    [InlineData(["02:03", 123])]
    [InlineData(["00:00:00", 0])]
    [InlineData(["1:00:00.500", 3600])] // fractional part dropped
    [InlineData(["", 0])]
    [InlineData([null, 0])]
    public void ToSeconds_ParsesTimecode(string? input, int expected)
    {
        input.ToSeconds().Should().Be(expected);
    }

    [Fact]
    public void TitleSort_StripsLeadingArticle()
    {
        "The Matrix".TitleSort((int?)null).Should().Be("matrix");
        "An Apple".TitleSort((int?)null).Should().Be("apple");
        "A Bridge".TitleSort((int?)null).Should().Be("bridge");
    }

    [Fact]
    public void TitleSort_InjectsYearAtColonSeparator()
    {
        "Title: Sub".TitleSort(2020).Should().Be("title.2020.sub");
    }

    [Theory]
    [InlineData(["HelloWorld", "hello_world"])]
    [InlineData(["ID", "i_d"])]
    [InlineData(["already_snake", "already_snake"])]
    public void ToSnakeCase_InsertsUnderscores(string input, string expected)
    {
        input.ToSnakeCase().Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello world", "Hello_World"])]
    [InlineData(["a b c", "A_B_C"])]
    public void ToPascalCase_TitleCasesAndJoinsWithUnderscore(string input, string expected)
    {
        input.ToPascalCase().Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello world", "Hello World"])]
    [InlineData(["THE QUICK", "The Quick"])]
    public void ToTitleCase_CapitalizesEachWord(string input, string expected)
    {
        input.ToTitleCase().Should().Be(expected);
    }

    [Fact]
    public void Capitalize_UppercasesFirstCharOnly()
    {
        "hello".Capitalize().Should().Be("Hello");
        "hELLO".Capitalize().Should().Be("HELLO");
    }

    [Fact]
    public void ToUcFirst_UppercasesFirstLowercasesRest()
    {
        "hELLO".ToUcFirst().Should().Be("Hello");
    }

    [Fact]
    public void SanitizeFileName_ReplacesSmartQuotesWithAscii()
    {
        "movie’s tale.mkv".SanitizeFileName().Should().Be("movie's tale.mkv");
    }

    [Theory]
    [InlineData(["12.7", 13])]
    [InlineData(["12", 12])]
    [InlineData(["", 0])]
    [InlineData(["abc", 0])]
    public void ToInt_RoundsOrZero(string input, int expected)
    {
        input.ToInt().Should().Be(expected);
    }

    [Theory]
    [InlineData(["1.5", 1.5])]
    [InlineData(["", 0.0])]
    [InlineData(["nope", 0.0])]
    public void ToDouble_ParsesOrZero(string input, double expected)
    {
        input.ToDouble().Should().Be(expected);
    }

    [Theory]
    [InlineData(["123", 123L])]
    [InlineData(["", 0L])]
    [InlineData(["x", 0L])]
    public void ToLong_ParsesOrZero(string input, long expected)
    {
        input.ToLong().Should().Be(expected);
    }

    [Theory]
    [InlineData(["true", true])]
    [InlineData(["false", false])]
    [InlineData(["", false])]
    [InlineData(["yes", false])] // only bool.TryParse-able strings count
    public void ToBoolean_ParsesOrFalse(string input, bool expected)
    {
        input.ToBoolean().Should().Be(expected);
    }

    [Fact]
    public void ContainsSanitized_IgnoresDiacriticsAndCase()
    {
        "Café Royale".ContainsSanitized("cafe").Should().BeTrue();
        "abc".ContainsSanitized(null).Should().BeFalse();
    }

    [Fact]
    public void ContainsSanitized_NonAsciiNeedle_DoesNotFalseMatchUnrelatedText()
    {
        // CJK/Cyrillic/Greek sanitize down to an empty string under the
        // ASCII-only regex; Contains("") must not make this trivially true.
        "The Matrix".ContainsSanitized("こんにちは").Should().BeFalse();
        "Some Album".ContainsSanitized("Привет").Should().BeFalse();
        "こんにちは".ContainsSanitized("hello").Should().BeFalse();
    }

    [Fact]
    public void ContainsSanitized_NonAsciiNeedle_StillMatchesRealContainment()
    {
        "こんにちは世界".ContainsSanitized("こんにちは").Should().BeTrue();
        "Привет мир".ContainsSanitized("Привет").Should().BeTrue();
    }

    [Fact]
    public void EqualsSanitized_MatchesAfterSanitization()
    {
        "The Matrix".EqualsSanitized("the matrix").Should().BeTrue();
        "Alpha".EqualsSanitized("Beta").Should().BeFalse();
    }

    [Fact]
    public void ToGuid_ReturnsEmptyOnMalformed()
    {
        "not-a-guid".ToGuid().Should().Be(Guid.Empty);
        Guid valid = Guid.NewGuid();
        valid.ToString().ToGuid().Should().Be(valid);
    }
}
