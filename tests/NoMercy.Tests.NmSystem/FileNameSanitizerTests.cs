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

using NoMercy.Storage.Drivers.Local;

namespace NoMercy.Tests.NmSystem;

[Trait("Category", "Unit")]
public class FileNameSanitizerTests
{
    [Fact]
    public void CleanFileName_WithEmptyString_ReturnsEmpty()
    {
        string result = "".CleanFileName();
        result.Should().Be("");
    }

    [Fact]
    public void CleanFileName_WithNull_ReturnsEmpty()
    {
        string result = ((string?)null).CleanFileName();
        result.Should().Be("");
    }

    [Fact]
    public void CleanFileName_WithNormalName_ReturnsUnchanged()
    {
        string result = "MovieTitle.mkv".CleanFileName();
        result.Should().Be("MovieTitle.mkv");
    }

    [Fact]
    public void CleanFileName_WithInvalidChars_ReplacesWithDots()
    {
        string result = "Movie|Title?.mkv".CleanFileName();
        result.Should().NotContain("|");
        result.Should().NotContain("?");
    }

    [Fact]
    public void CleanFileName_WithWhitespace_ReplacesWithDots()
    {
        string result = "Movie Title.mkv".CleanFileName();
        result.Should().Contain(".");
    }

    [Fact]
    public void CleanFileName_WithAmpersand_ReplacesWithAnd()
    {
        string result = "Movie&Title.mkv".CleanFileName();
        result.Should().Contain("and");
    }

    [Fact]
    public void CleanFileName_WithDegreesSign_ReplacesWithText()
    {
        string result = "90°.mkv".CleanFileName();
        result.Should().Contain("Degrees");
    }

    [Fact]
    public void CleanFileName_WithDashes_ReplacesManyWithOne()
    {
        string result = "Movie—Title–Test–Case.mkv".CleanFileName();
        result.Should().Contain("-");
    }

    [Fact]
    public void CleanFileName_WithMultipleDots_CollapsesToOne()
    {
        string result = "Movie...Title...Name.mkv".CleanFileName();
        result.Should().NotContain("...");
    }

    [Fact]
    public void CleanFileName_WithLeadingTrailingDots_RemovesThem()
    {
        string result = ".MovieTitle.".CleanFileName();
        result.Should().NotStartWith(".");
        result.Should().NotEndWith(".");
    }

    [Fact]
    public void DirectorySafeName_WithNull_ReturnsEmpty()
    {
        string result = ((string?)null).DirectorySafeName();
        result.Should().Be("");
    }

    [Fact]
    public void DirectorySafeName_WithNormalName_ReturnsName()
    {
        string result = "MyFolder".DirectorySafeName();
        result.Should().Be("MyFolder");
    }

    [Fact]
    public void DirectorySafeName_WithInvalidChars_ReplacesWithSpaces()
    {
        string result = "Folder/Name|Other".DirectorySafeName();
        result.Should().NotContain("/");
        result.Should().NotContain("|");
    }

    [Fact]
    public void MusicBrainzSafeName_WithNull_ReturnsEmpty()
    {
        string result = ((string?)null).MusicBrainzSafeName();
        result.Should().Be("");
    }

    [Fact]
    public void MusicBrainzSafeName_WithNormalName_ReturnsName()
    {
        string result = "ArtistName".MusicBrainzSafeName();
        result.Should().Be("ArtistName");
    }

    [Fact]
    public void MusicBrainzSafeName_WithInvalidChars_ReplacesWithUnderscores()
    {
        string result = "Artist/Name|Other".MusicBrainzSafeName();
        result.Should().NotContain("/");
        result.Should().NotContain("|");
    }

    [Fact]
    public void SanitizeFileName_WithSmartQuotes_ConvertsToAscii()
    {
        string result = "movie’s tale.mkv".SanitizeFileName();
        result.Should().Contain("'");
    }

    [Fact]
    public void SanitizeFileName_WithLeftDoubleQuote_ConvertsToAscii()
    {
        string result = "“movie”.mkv".SanitizeFileName();
        result.Should().Contain("\"");
    }

    [Fact]
    public void SanitizeFileName_WithEnDash_ConvertsToHyphen()
    {
        string result = "movie–name.mkv".SanitizeFileName();
        result.Should().Contain("-");
    }

    [Theory]
    [InlineData(["simple", "simple"])]
    [InlineData(["with spaces", "withspaces"])]
    [InlineData(["with-dashes", "withdashes"])]
    [InlineData(["CamelCase", "camelcase"])]
    public void NormalizeForComparison_StripsNonAlphanumeric(string input, string expected)
    {
        string result = input.NormalizeForComparison();
        result.Should().Be(expected);
    }

    [Fact]
    public void NormalizeForComparison_ConvertAmpersand()
    {
        string result = "A&B".NormalizeForComparison();
        result.Should().Be("aandb");
    }

    [Fact]
    public void NormalizeForComparison_KeepsNonLatinLetters()
    {
        "進撃の巨人".NormalizeForComparison().Should().NotBeEmpty();
        "進撃の巨人".NormalizeForComparison().Should().NotBe("鬼滅の刃".NormalizeForComparison());
        "Привет".NormalizeForComparison().Should().Be("привет");
    }

    [Fact]
    public void FindMatchingDirectory_TwoNonLatinFolders_DoesNotReturnTheOtherOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "nm-379-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "鬼滅の刃"));
        Directory.CreateDirectory(Path.Combine(root, "進撃の巨人"));
        try
        {
            LocalStorageDriver driver = new();

            string? other = FileNameSanitizer.FindMatchingDirectory(driver, root, "ワンピース");
            string? same = FileNameSanitizer.FindMatchingDirectory(driver, root, "進撃の巨人");

            other.Should().BeNull();
            same.Should().Be(Path.Combine(root, "進撃の巨人"));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void FindMatchingDirectory_EmptyNormalizedName_ReturnsNull()
    {
        string root = Path.Combine(Path.GetTempPath(), "nm-379-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "---"));
        try
        {
            FileNameSanitizer.FindMatchingDirectory(new LocalStorageDriver(), root, "...").Should().BeNull();
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void NormalizeForComparison_IsCaseInsensitive()
    {
        string result = "HELLO".NormalizeForComparison();
        result.Should().Be("hello");
    }

    [Fact]
    public void Shorten_WithinLimit_ReturnsUnchanged()
    {
        // Short titles keep their exact existing path — no relocation of content.
        "Breaking Bad".Shorten().Should().Be("Breaking Bad");
    }

    [Fact]
    public void Shorten_NullOrEmpty_ReturnsEmpty()
    {
        ((string?)null).Shorten().Should().Be("");
        "".Shorten().Should().Be("");
    }

    [Fact]
    public void Shorten_LongInput_IsBoundedToMaxLength()
    {
        string result = new string('a', 200).Shorten();
        result.Length.Should().BeLessThanOrEqualTo(FileNameSanitizer.MaxTitleComponentLength);
    }

    [Fact]
    public void Shorten_IsDeterministic()
    {
        const string longTitle =
            "My.Gift.Lvl.9999.Unlimited.Gacha.Backstabbed.in.a.Backwater.Dungeon.Im.Out.for.Revenge";
        longTitle.Shorten().Should().Be(longTitle.Shorten());
    }

    [Fact]
    public void Shorten_LongTitle_IsStillReadable_AndCarriesNoDigest()
    {
        // The name a viewer reads is the start of the real title and nothing else.
        // Two titles sharing a 50-char prefix do now produce the same component;
        // what separates them lives in the name this is embedded in — an episode's
        // SxxEyy marker, a movie's release year — which is where it belonged.
        string cleaned =
            "OO Magic Episode 3: The Magic of Waking Up at a Certain Time in the Morning".CleanFileName();

        string result = cleaned.Shorten();

        result.Should().Be("OO.Magic.Episode.3.The.Magic.of.Waking.Up.at.a");
        result.Should().NotMatchRegex("[0-9a-f]{8}$", "a digest is not part of a title");
    }

    [Fact]
    public void Shorten_UsesTheWholeBudgetForTheTitle()
    {
        // The digest used to eat nine of the fifty characters, so a title was cut
        // far shorter than the limit actually required.
        string cleaned = new string('a', 60);

        cleaned
            .Shorten()
            .Length.Should()
            .Be(FileNameSanitizer.MaxTitleComponentLength, "every character of the cap is title");
    }

    [Fact]
    public void Shorten_RealAnimeTitle_FitsAndKeepsRecognizablePrefix()
    {
        string cleaned =
            "My Gift LVL 9999 Unlimited Gacha Backstabbed in a Backwater Dungeon Im Out for Revenge".CleanFileName();

        string result = cleaned.Shorten();

        result.Length.Should().BeLessThanOrEqualTo(FileNameSanitizer.MaxTitleComponentLength);
        result.Should().StartWith("My.Gift");
    }
}
