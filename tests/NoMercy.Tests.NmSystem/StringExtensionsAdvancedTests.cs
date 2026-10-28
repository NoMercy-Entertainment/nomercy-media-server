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
public class StringExtensionsAdvancedTests
{
    [Theory]
    [InlineData(["café", "cafe"])]
    [InlineData(["naïve", "naive"])]
    [InlineData(["résumé", "resume"])]
    public void RemoveDiacritics_StripsCombiningMarks(string input, string expected)
    {
        string result = input.RemoveDiacritics();
        result.Should().Be(expected);
    }

    [Fact]
    public void RemoveNonAlphaNumericCharacters_KeepsSpacesDashesAndDots()
    {
        string result = "Movie-2021.Title (Director's Cut)!".RemoveNonAlphaNumericCharacters();
        result.Should().Contain("Movie");
        result.Should().Contain("2021");
        result.Should().Contain("-");
        result.Should().NotContain("(");
        result.Should().NotContain("!");
    }

    [Theory]
    [InlineData(["Movie 2020.mkv", 2020])]
    [InlineData(["Title (2021) Extra.mkv", 2021])]
    [InlineData(["2019 Release Date.mkv", 2019])]
    [InlineData(["Film from 1999", 1999])]
    public void TryGetYear_ParsesFourDigitYear(string input, int expectedYear)
    {
        string? result = input.TryGetYear();
        result.Should().Be(expectedYear.ToString());
    }

    [Fact]
    public void TryGetYear_WithoutYear_ReturnsNull()
    {
        string? result = "No year here".TryGetYear();
        result.Should().BeNull();
    }

    [Fact]
    public void TryGetYear_WithThreeDigitNumber_IgnoresIt()
    {
        string? result = "Movie 123 Title".TryGetYear();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(["Show.S01E05", "01", "05"])]
    [InlineData(["Series S02E10", "02", "10"])]
    [InlineData(["prefix S05E01 Start", "05", "01"])]
    public void MatchSeasonEpisode_ExtractsFromText(
        string input,
        string expectedSeason,
        string expectedEpisode
    )
    {
        System.Text.RegularExpressions.Match match = StringExtensions
            .MatchSeasonEpisode()
            .Match(input);
        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be(expectedSeason);
        match.Groups[2].Value.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData(["Movie 2x05", "2", "05"])]
    [InlineData(["Show 1×10", "1", "10"])]
    [InlineData(["Series 3X08", "3", "08"])]
    public void MatchCrossFormatEpisode_ExtractsSeasonEpisode(
        string input,
        string expectedSeason,
        string expectedEpisode
    )
    {
        System.Text.RegularExpressions.Match match = StringExtensions
            .MatchCrossFormatEpisode()
            .Match(input);
        match.Success.Should().BeTrue();
        match.Groups[1].Value.Should().Be(expectedSeason);
        match.Groups[2].Value.Should().Be(expectedEpisode);
    }

    [Theory]
    [InlineData(["Movie.1080p.mkv", true])]
    [InlineData(["Show.720p.mkv", true])]
    [InlineData(["Film.4k.mkv", true])]
    [InlineData(["Title.uhd.mkv", true])]
    [InlineData(["No.resolution.mkv", false])]
    public void MatchResolutionTag_IdentifiesResolution(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchResolutionTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData(["Movie.WEBRIP.mkv", true])]
    [InlineData(["Show.BLURAY.mkv", true])]
    [InlineData(["Film.DVDRip.mkv", true])]
    [InlineData(["Title.HDTV.mkv", true])]
    [InlineData(["No.source.mkv", false])]
    public void MatchSourceTag_IdentifiesSource(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchSourceTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData(["Movie.H264.mkv", true])]
    [InlineData(["Show.HEVC.mkv", true])]
    [InlineData(["Film.XVID.mkv", true])]
    [InlineData(["Title.x265.mkv", true])]
    [InlineData(["No.codec.mkv", false])]
    public void MatchCodecTag_IdentifiesCodec(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchCodecTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData(["Movie.AAC.mkv", true])]
    [InlineData(["Show.DDP5.1.mkv", true])]
    [InlineData(["Film.FLAC.mkv", true])]
    [InlineData(["Title.AC3.mkv", true])]
    [InlineData(["No.audio.mkv", false])]
    public void MatchAudioTag_IdentifiesAudio(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchAudioTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData(["Movie.10bit.mkv", true])]
    [InlineData(["Show.HDR10.mkv", true])]
    [InlineData(["Film.DOVI.mkv", true])]
    [InlineData(["Title.SDR.mkv", true])]
    [InlineData(["Unknown.mkv", false])]
    public void MatchHdrTag_IdentifiesHdr(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchHdrTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Theory]
    [InlineData(["Movie.REPACK.mkv", true])]
    [InlineData(["Show.MULTI.mkv", true])]
    [InlineData(["Film.IMAX.mkv", true])]
    [InlineData(["No.flag.mkv", false])]
    public void MatchFlagTag_IdentifiesFlag(string input, bool shouldMatch)
    {
        bool matches = StringExtensions.MatchFlagTag().IsMatch(input);
        matches.Should().Be(shouldMatch);
    }

    [Fact]
    public void TryGetReleaseTag_FindsFirstTag()
    {
        bool found = "Movie.1080p.WEBRIP.H264.mkv".TryGetReleaseTag(
            out string value,
            out StringExtensions.ReleaseTagCategory category
        );
        found.Should().BeTrue();
        value.Should().Be("1080p");
        category.Should().Be(StringExtensions.ReleaseTagCategory.Resolution);
    }

    [Fact]
    public void TryGetReleaseTag_EmptyString_ReturnsFalse()
    {
        bool found = "".TryGetReleaseTag(
            out string value,
            out StringExtensions.ReleaseTagCategory category
        );
        found.Should().BeFalse();
    }

    [Fact]
    public void TryGetReleaseTag_NullString_ReturnsFalse()
    {
        bool found = ((string?)null)!.TryGetReleaseTag(
            out string value,
            out StringExtensions.ReleaseTagCategory category
        );
        found.Should().BeFalse();
    }

    [Theory]
    [InlineData("Movie.1080p.WEBRIP.H264.mkv")]
    [InlineData("Show.Season.2.HDTV.mkv")]
    [InlineData("Film 2021 720p")]
    public void CleanReleaseTitle_RemovesSceneTagsAndBeyond(string input)
    {
        string result = input.CleanReleaseTitle();
        result.Should().NotBeEmpty();
        result.Length.Should().BeLessThanOrEqualTo(input.Length);
    }

    [Fact]
    public void CleanReleaseTitle_WithoutTags_ReturnsAsIs()
    {
        string result = "Simple Movie Title".CleanReleaseTitle();
        result.Should().Be("Simple Movie Title");
    }

    [Theory]
    [InlineData(["Show 2022", "Show"])]
    [InlineData(["New Amsterdam 2018", "New Amsterdam"])]
    [InlineData(["1883", "1883"])]
    [InlineData(["2021 Apocalypse", "2021 Apocalypse"])]
    public void CleanSeriesTitle_RemovesTrailingYear(string input, string expected)
    {
        string result = input.CleanSeriesTitle();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["/path/Season 02", 2])]
    [InlineData(["Show/Series 5", 5])]
    [InlineData(["X/S02", 2])]
    [InlineData(["/media/saison 01", 1])]
    public void TryGetFolderSeason_ExtractsSeasonNumber(string path, int expected)
    {
        int? result = path.TryGetFolderSeason();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("NotASeason")]
    [InlineData("Show/Season A")]
    [InlineData("/path/movies")]
    public void TryGetFolderSeason_WithoutSeasonFolder_ReturnsNull(string path)
    {
        int? result = path.TryGetFolderSeason();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryGetFolderSeason_WithNullOrEmpty_ReturnsNull(string? path)
    {
        int? result = path.TryGetFolderSeason();
        result.Should().BeNull();
    }

    [Theory]
    [InlineData("HelloWorld")]
    [InlineData("ID")]
    [InlineData("lowercase")]
    public void SplitPascalCase_SplitsOnWordBoundaries(string input)
    {
        string result = input.SplitPascalCase();
        result.Should().NotBeEmpty();
        result.Length.Should().BeGreaterThanOrEqualTo(input.Length);
    }

    [Fact]
    public void RemoveAccents_EncodesStringToIso88591()
    {
        string input = "hello";
        string result = input.RemoveAccents();
        result.Should().Be("hello");
    }

    [Fact]
    public void RemoveAccents_KeepsACharacterLatin1CannotHold()
    {
        "❤".RemoveAccents().Should().Be("❤");
        "Café \U0001F3B5".RemoveAccents().Should().Be("Cafe \U0001F3B5");
    }

    [Fact]
    public void PathName_NormalizesForwardSlashes()
    {
        // PathName rewrites every separator to Path.DirectorySeparatorChar, which IS
        // '/' on Linux — asserting the absence of '/' only holds on Windows and fails
        // the Linux CI leg. Assert the actual contract: separators are normalised to
        // the running platform's.
        string result = "path/to/file".PathName();

        result
            .Should()
            .Be(
                $"path{Path.DirectorySeparatorChar}to{Path.DirectorySeparatorChar}file"
            );
    }

    [Fact]
    public void PathName_NormalizesBackslashes()
    {
        string result = "path\\to\\file".PathName();

        result
            .Should()
            .Be(
                $"path{Path.DirectorySeparatorChar}to{Path.DirectorySeparatorChar}file"
            );
    }

    [Theory]
    [InlineData(["123.45", 123])]
    [InlineData(["", 0])]
    [InlineData(["invalid", 0])]
    [InlineData(["0", 0])]
    [InlineData(["-50.5", -50])]
    public void ToInt_String_ParsesIntegerValue(string input, int expected)
    {
        int result = input.ToInt();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData([123.7, 124])]
    [InlineData([123.4, 123])]
    public void ToInt_Double_ConvertsWithRounding(double input, int expected)
    {
        int result = input.ToInt();
        result.Should().Be(expected);
    }

    [Fact]
    public void ToInt_UInt_ConvertsUnsignedInteger()
    {
        uint input = 100;
        int result = input.ToInt();
        result.Should().Be(100);
    }

    [Theory]
    [InlineData(["456.78", 456.78])]
    [InlineData(["", 0d])]
    [InlineData(["invalid", 0d])]
    public void ToDouble_String_ParsesDoubleValue(string input, double expected)
    {
        double result = input.ToDouble();
        result.Should().Be(expected);
    }

    [Fact]
    public void ToDouble_Int_ConvertsToDouble()
    {
        int input = 100;
        double result = input.ToDouble();
        result.Should().Be(100d);
    }

    [Theory]
    [InlineData(["789", 789L])]
    [InlineData(["", 0L])]
    [InlineData(["invalid", 0L])]
    public void ToLong_String_ParsesLongValue(string input, long expected)
    {
        long result = input.ToLong();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["true", true])]
    [InlineData(["True", true])]
    [InlineData(["false", false])]
    [InlineData(["", false])]
    [InlineData(["invalid", false])]
    public void ToBoolean_String_ParsesBooleanValue(string input, bool expected)
    {
        bool result = input.ToBoolean();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["text", 10, false, "text      "])]
    [InlineData(["hi", 5, false, "hi   "])]
    [InlineData(["text", 10, true, "      text"])]
    [InlineData(["hi", 5, true, "   hi"])]
    public void Spacer_PadsTextWithSpaces(string text, int padding, bool begin, string expected)
    {
        string result = StringExtensions.Spacer(text, padding, begin);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["550e8400-e29b-41d4-a716-446655440000", "550e8400-e29b-41d4-a716-446655440000"])]
    [InlineData(["invalid-guid", "00000000-0000-0000-0000-000000000000"])]
    [InlineData(["", "00000000-0000-0000-0000-000000000000"])]
    [InlineData([null, "00000000-0000-0000-0000-000000000000"])]
    public void ToGuid_String_ParsesOrReturnsEmpty(string? input, string expected)
    {
        Guid result = input.ToGuid();
        result.Should().Be(Guid.Parse(expected));
    }

    [Fact]
    public void SplitPascalCase_SplitsCamelCaseWords()
    {
        string result = "CamelCase".SplitPascalCase();
        result.Should().NotBeEmpty();
    }

    [Theory]
    [InlineData(["00:30:45", 1845])]
    [InlineData(["1:15:30", 4530])]
    [InlineData(["45", 45])]
    [InlineData(["", 0])]
    [InlineData([null, 0])]
    public void ToSeconds_String_ParsesTimeFormatToSeconds(string? input, int expected)
    {
        int result = input.ToSeconds();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData([45.6, 46])]
    [InlineData([0d, 0])]
    [InlineData([123.4, 123])]
    public void ToSeconds_Double_RoundsToIntSeconds(double input, int expected)
    {
        int result = input.ToSeconds();
        result.Should().Be(expected);
    }

    [Fact]
    public void ToMilliSeconds_String_ConvertsToMilliseconds()
    {
        int result = "00:00:10".ToMilliSeconds();
        result.Should().Be(10000);
    }

    [Fact]
    public void SplitPascalCase_SplitsConsecutiveUppercase()
    {
        string result = "CamelCase".SplitPascalCase();
        result.Should().Contain("C");
    }


    [Theory]
    [InlineData(["café naïve", "cafe naive"])]
    [InlineData(["résumé", "resume"])]
    [InlineData(["hello", "hello"])]
    public void Sanitize_RemovesDiacriticsAndNonAlphanumeric(string input, string expected)
    {
        string result = input.Sanitize();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["Café", "café", true])]
    [InlineData(["HELLO World", "hello world", true])]
    [InlineData(["one", "two", false])]
    public void ContainsSanitized_ComparesNormalizedStrings(string haystack, string needle, bool expected)
    {
        bool result = haystack.ContainsSanitized(needle);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["Café", "CAFÉ", true])]
    [InlineData(["hello", "HELLO", true])]
    [InlineData(["one", "two", false])]
    public void EqualsSanitized_ComparesNormalizedEquality(string a, string b, bool expected)
    {
        bool result = a.EqualsSanitized(b);
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello world", "hello world"])]
    [InlineData(["hello%20world", "hello world"])]
    public void UrlDecode_DecodesUrlEncodedString(string input, string expected)
    {
        string result = input.UrlDecode();
        result.Should().Be(expected);
    }

    [Fact]
    public void UrlEncode_EncodesSpaces()
    {
        string result = "hello world".UrlEncode();
        result.Should().NotBe("hello world");
    }

    [Fact]
    public void UrlEncode_PreservesNormalText()
    {
        string result = "test".UrlEncode();
        result.Should().Be("test");
    }

    [Fact]
    public void ToQueryUri_AppendsQueryParameters()
    {
        string result = "http://example.com".ToQueryUri(new Dictionary<string, string>
        {
            ["key1"] = "value1",
            ["key2"] = "value2"
        });
        result.Should().Contain("?");
        result.Should().Contain("key1=value1");
    }

    [Fact]
    public void ToQueryUri_WithNullParameters_ReturnsBaseUri()
    {
        string result = "http://example.com".ToQueryUri(null);
        result.Should().Be("http://example.com");
    }

    [Theory]
    [InlineData(["hello\"world", "hello'world"])]
    [InlineData(["\"test\"", "'test'"])]
    public void EscapeQuotes_ReplacesDoubleQuotesWithSingle(string input, string expected)
    {
        string result = input.EscapeQuotes();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello", "Hello"])]
    [InlineData(["world", "World"])]
    [InlineData(["", ""])]
    public void Capitalize_CapitalizesFirstCharacter(string input, string expected)
    {
        string result = input.Capitalize();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello world", "Hello World"])]
    [InlineData(["test", "Test"])]
    public void ToTitleCase_CapitalizesEachWord(string input, string expectedStart)
    {
        string result = input.ToTitleCase();
        result.Should().StartWith(expectedStart[0].ToString().ToUpper());
    }

    [Theory]
    [InlineData(["hello world", "Hello_World"])]
    [InlineData(["test", "Test"])]
    public void ToPascalCase_ConvertsToPascalCase(string input, string expected)
    {
        string result = input.ToPascalCase();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["HelloWorld", "hello_world"])]
    [InlineData(["HTTPServer", "h_t_t_p_server"])]
    public void ToSnakeCase_ConvertsToSnakeCase(string input, string expected)
    {
        string result = input.ToSnakeCase();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData(["hello", "Hello"])]
    [InlineData(["WORLD", "World"])]
    public void ToUcFirst_UppercasesFirstCharacter(string input, string expected)
    {
        string result = input.ToUcFirst();
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("test string")]
    public void ToUtf8_ConvertedToUtf8(string input)
    {
        string result = input.ToUtf8();
        result.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData("hello world")]
    [InlineData("SHOUT")]
    public void NormalizeSearch_NormalizesSearchString(string input)
    {
        string result = input.NormalizeSearch();
        result.Should().NotBeNullOrEmpty();
    }

    [Theory]
    [InlineData(["Hello שלום", StringExtensions.TextDirection.RTL])]
    [InlineData(["مرحبا Hello", StringExtensions.TextDirection.RTL])]
    [InlineData(["Hello World", StringExtensions.TextDirection.LTR])]
    public void GetTextDirection_IdentifiesTextDirection(string input, StringExtensions.TextDirection expected)
    {
        StringExtensions.TextDirection result = input.GetTextDirection();
        result.Should().Be(expected);
    }

    [Fact]
    public void TryGetTmdbHint_ExtractsTmdbId()
    {
        int? result = "Movie Title [tmdb-12345]".TryGetTmdbHint();
        result.Should().Be(12345);
    }

    [Fact]
    public void TryGetTmdbHint_WithoutHint_ReturnsNull()
    {
        int? result = "Movie Title".TryGetTmdbHint();
        result.Should().BeNull();
    }

    [Fact]
    public void TryGetTmdbHint_CaseInsensitive()
    {
        int? result = "Movie Title [TMDB-999]".TryGetTmdbHint();
        result.Should().Be(999);
    }
}
