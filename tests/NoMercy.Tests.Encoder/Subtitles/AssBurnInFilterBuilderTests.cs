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

using NoMercy.Encoder.Subtitles;

namespace NoMercy.Tests.Encoder.Subtitles;

/// <summary>
/// AssBurnInFilterBuilder produces the correct <c>ass=</c> filter
/// expression with FFmpeg filter-graph escaping applied.
/// </summary>
public class AssBurnInFilterBuilderTests
{
    private readonly AssBurnInFilterBuilder _builder = new();

    [Fact]
    public void Build_SimplePath_ReturnsAssFilter()
    {
        string result = _builder.Build("/path/to/subtitle.ass");

        Assert.Equal("ass='/path/to/subtitle.ass'", result);
    }

    [Fact]
    public void Build_PathWithColon_EscapesColon()
    {
        // Windows-style path with drive letter colon
        string result = _builder.Build("C:/movies/subtitle.ass");

        Assert.Contains("C\\:/movies/subtitle.ass", result);
    }

    [Fact]
    public void Build_PathWithBackslashes_NormalisesToForwardSlashThenEscapesColon()
    {
        // Windows path with backslash separators
        string result = _builder.Build(@"C:\movies\my film\subtitle.ass");

        // Backslashes normalised to forward slashes; colon on drive letter escaped
        Assert.Contains("C\\:/movies/my film/subtitle.ass", result);
    }

    [Fact]
    public void Build_WithFontDirectory_AppendsFontsDirOption()
    {
        string result = _builder.Build("/path/to/subtitle.ass", "/output/fonts");

        Assert.StartsWith("ass='", result);
        Assert.Contains(":fontsdir='/output/fonts'", result);
    }

    [Fact]
    public void Build_WithNullFontDirectory_OmitsFontsDirOption()
    {
        string result = _builder.Build("/path/to/subtitle.ass", fontDirectory: null);

        Assert.DoesNotContain("fontsdir", result);
    }

    [Fact]
    public void Build_WithEmptyFontDirectory_OmitsFontsDirOption()
    {
        string result = _builder.Build("/path/to/subtitle.ass", fontDirectory: "");

        Assert.DoesNotContain("fontsdir", result);
    }

    [Fact]
    public void Build_PathWithColonInFilename_EscapesAllColons()
    {
        // Edge case: filename that contains a colon (non-Windows, unusual but valid)
        string result = _builder.Build("/media/show:s01e01.ass");

        Assert.Contains("/media/show\\:s01e01.ass", result);
    }

    [Fact]
    public void Build_PathWithSpacesCommaSemicolonAndBrackets_SurvivesUnescapedInsideQuotes()
    {
        // Verified against a real ffmpeg build: comma / semicolon / brackets /
        // whitespace need no individual escaping once the value is quoted —
        // only the colon, backslash, and quote characters inside the quotes do.
        string result = _builder.Build("/media/[Group] show, part;1.ass");

        Assert.Equal("ass='/media/[Group] show, part;1.ass'", result);
    }

    [Fact]
    public void Build_PathWithApostrophe_KeepsFontsDirAsASeparateOption()
    {
        // Verified against a real ffmpeg build: an apostrophe escaped inside
        // the quotes, or escaped for one parse level only, makes libass read
        // ":fontsdir=..." as part of the file name. The path must close its
        // quote, emit \\\' and reopen so fontsdir survives as its own option.
        string result = _builder.Build("/media/it's a show.ass", "/output/fonts");

        Assert.Equal("ass='/media/it'\\\\\\''s a show.ass':fontsdir='/output/fonts'", result);
    }
}
