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

using NoMercy.Encoder.PostProcess;

namespace NoMercy.Tests.Encoder.PostProcess;

/// <summary>
/// The sheet's filename is the only record of the tile size it was rendered at,
/// which is what lets a scan tell an old preview from a current one without a
/// database column or a probe. These pin that reading.
/// </summary>
public class SpriteSheetTests
{
    [Theory]
    [InlineData("thumbs_320x180.webp", 320)]
    [InlineData("thumbs_160x90.webp", 160)]
    [InlineData("thumbs_320x144.webp", 320)] // 2.39:1 film — height follows the source
    [InlineData("THUMBS_320X180.WEBP", 320)]
    public void ReadTileWidth_ReadsTheWidthOutOfASheetName(string fileName, int expected) =>
        SpriteSheet.ReadTileWidth(fileName).Should().Be(expected);

    [Theory]
    [InlineData("thumbs_320x180.vtt")] // the cue file, not the sheet
    [InlineData("chapters.vtt")]
    [InlineData("thumbs_320.webp")] // pre-height naming, no tile size to trust
    [InlineData("video_1920x1080_sdr.m3u8")]
    [InlineData("poster.webp")]
    public void ReadTileWidth_IsNullForAnythingThatIsNotASheet(string fileName) =>
        SpriteSheet.ReadTileWidth(fileName).Should().BeNull();

    [Fact]
    public void SelectUndersized_ReturnsTheNarrowSheet()
    {
        IReadOnlyList<string> undersized = SpriteSheet.SelectUndersized(
            ["thumbs_160x90.webp", "thumbs_160x90.vtt", "chapters.vtt"],
            minimumWidth: 320
        );

        undersized.Should().ContainSingle().Which.Should().Be("thumbs_160x90.webp");
    }

    [Fact]
    public void SelectUndersized_IsEmptyWhenTheSheetIsWideEnough()
    {
        SpriteSheet
            .SelectUndersized(["thumbs_320x180.webp", "thumbs_320x180.vtt"], minimumWidth: 320)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void SelectUndersized_IsEmptyWhenAWideSheetSitsBesideALeftoverNarrowOne()
    {
        // A previous render that was never cleaned up. There is a current sheet,
        // so there is nothing to rebuild — queuing a job here would re-render the
        // same title on every scan, forever.
        SpriteSheet
            .SelectUndersized(
                ["thumbs_160x90.webp", "thumbs_320x180.webp", "thumbs_320x180.vtt"],
                minimumWidth: 320
            )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void SelectUndersized_IsEmptyWhenThereIsNoSheetAtAll()
    {
        // No preview is a job for the encoder's own thumbnail pass, which knows
        // how to make one from scratch. Claiming it here would queue an upgrade
        // for a title that has nothing to upgrade.
        SpriteSheet
            .SelectUndersized(["web-1080p_master.m3u8", "chapters.vtt"], minimumWidth: 320)
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void SelectUndersized_ClaimsALegacySheetThatStatesNoTileSize()
    {
        // `sprite.webp` is what titles encoded before the tile size went into the
        // name still carry, and it never has a cue file beside it, so the scrub
        // preview is blank for the whole title. It reads as no sheet at all
        // unless this claims it, and then no rebuild is ever queued.
        IReadOnlyList<string> undersized = SpriteSheet.SelectUndersized(
            ["sprite.webp", "chapters.vtt"],
            minimumWidth: 320
        );

        undersized.Should().ContainSingle().Which.Should().Be("sprite.webp");
    }

    [Fact]
    public void SelectUndersized_IsEmptyWhenACurrentSheetSitsBesideALegacyOne()
    {
        // Re-rendered already; the legacy file is leftover, not work.
        SpriteSheet
            .SelectUndersized(
                ["sprite.webp", "thumbs_320x180.webp", "thumbs_320x180.vtt"],
                minimumWidth: 320
            )
            .Should()
            .BeEmpty();
    }

    [Fact]
    public void MinimumWidth_IsTheDefaultEveryPresetInherits() =>
        new NoMercy.Encoder.Profiles.HlsDerivatives()
            .SpriteVttThumbnailWidth.Should()
            .Be(
                SpriteSheet.MinimumWidth,
                "the floor a scan upgrades to and the width an encode renders at must be one number"
            );
}
