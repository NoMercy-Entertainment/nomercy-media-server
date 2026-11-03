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

using NoMercy.Encoder.BuildingBlocks;

namespace NoMercy.Tests.Encoder.BuildingBlocks;

/// <summary>
/// FilterGraphPathEscaper is the single shared implementation for escaping a
/// path spliced into an FFmpeg filtergraph option value (lut3d=, ass=,
/// subtitles=). These tests assert the escaped output byte-for-byte so a
/// regression that stops escaping a metacharacter — or drops the quoting —
/// fails loudly. The quoting/escaping combination is verified against a real
/// ffmpeg build: quoting alone does not neutralise a drive-letter colon
/// (ffmpeg misreads the option boundary), escaping without quoting breaks
/// graph-level comma/semicolon/bracket parsing, and an apostrophe escaped
/// inside the quotes (or escaped for one parse level only) swallows every
/// option that follows the path. Both together are required.
/// </summary>
public class FilterGraphPathEscaperTests
{
    [Fact]
    public void Escape_PlainUnixPath_WrapsInQuotesUnchanged()
    {
        string result = FilterGraphPathEscaper.Escape("/usr/media/sub.ass");

        Assert.Equal("'/usr/media/sub.ass'", result);
    }

    [Fact]
    public void Escape_WindowsDriveColon_EscapesColonAfterNormalisingSeparators()
    {
        string result = FilterGraphPathEscaper.Escape(@"C:\movies\file.ass");

        Assert.Equal("'C\\:/movies/file.ass'", result);
    }

    [Fact]
    public void Escape_PathWithSingleQuote_ClosesQuoteEscapesApostropheAndReopens()
    {
        // ffmpeg does not process escapes inside single quotes, so a quoted \'
        // closes the value at the apostrophe and the rest of the filter chain
        // is swallowed into the path. The value must close its quote, emit the
        // apostrophe escaped for both parse levels (\\\' -> \' at the graph
        // level -> ' at the option level), then reopen.
        string result = FilterGraphPathEscaper.Escape("/media/it's a show.ass");

        Assert.Equal("'/media/it'\\\\\\''s a show.ass'", result);
    }

    [Fact]
    public void Escape_PathWithEqualsSign_EscapesEqualsSoItIsNotReadAsAnOptionKey()
    {
        // The option-level parser reads "key=value" first. A path that starts
        // with key characters (every Linux absolute path) and contains '='
        // is split at the '=' and rejected as an unknown option.
        string result = FilterGraphPathEscaper.Escape("/media/a=b.ass");

        Assert.Equal("'/media/a\\=b.ass'", result);
    }

    [Fact]
    public void Escape_PathWithBrackets_SurvivesUnescapedInsideQuotes()
    {
        string result = FilterGraphPathEscaper.Escape("/media/[Group]release.ass");

        Assert.Equal("'/media/[Group]release.ass'", result);
    }

    [Fact]
    public void Escape_PathWithCommaAndSemicolon_SurvivesUnescapedInsideQuotes()
    {
        string result = FilterGraphPathEscaper.Escape("/media/a,b;c.ass");

        Assert.Equal("'/media/a,b;c.ass'", result);
    }

    [Fact]
    public void Escape_PathWithSpaces_SurvivesUnescapedInsideQuotes()
    {
        string result = FilterGraphPathEscaper.Escape("/movies/my film/subtitle.ass");

        Assert.Equal("'/movies/my film/subtitle.ass'", result);
    }

    [Fact]
    public void Escape_CombinedSpecialCharacters_EscapesOnlyOptionLevelMetacharacters()
    {
        string result = FilterGraphPathEscaper.Escape(@"C:\media\[grp] it's a, show;x=1.ass");

        Assert.Equal("'C\\:/media/[grp] it'\\\\\\''s a, show;x\\=1.ass'", result);
    }
}
