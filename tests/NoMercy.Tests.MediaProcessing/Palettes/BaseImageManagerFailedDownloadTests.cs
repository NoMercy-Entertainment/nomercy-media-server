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

using ImageMagick;
using NoMercy.MediaProcessing.Images;

namespace NoMercy.Tests.MediaProcessing.Palettes;

[Trait("Category", "Unit")]
public class BaseImageManagerFailedDownloadTests
{
    [Fact]
    public void GenerateColorPalette_omits_a_key_whose_image_is_null()
    {
        string json = BaseImageManager.GenerateColorPalette([
            new() { Key = "poster", ImageData = null },
        ]);

        json.Should().NotContain("poster");
    }

    [Fact]
    public async Task MultiColorPalette_throws_when_every_requested_download_failed()
    {
        BaseImageManager.DownloadPath failing = (_, _, _) => Task.FromResult<MagickImage?>(null);

        Func<Task> act = () =>
            BaseImageManager.MultiColorPalette(
                failing,
                [new("poster", "/poster.jpg"), new("backdrop", "/backdrop.jpg")]
            );

        await act.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task MultiColorPalette_returns_empty_when_no_image_path_was_requested()
    {
        BaseImageManager.DownloadPath failing = (_, _, _) => Task.FromResult<MagickImage?>(null);

        string json = await BaseImageManager.MultiColorPalette(
            failing,
            [new("poster", null), new("backdrop", "")]
        );

        json.Should().Be("{}");
    }

    [Fact]
    public async Task ColorPalette_throws_when_the_download_failed()
    {
        BaseImageManager.DownloadPath failing = (_, _, _) => Task.FromResult<MagickImage?>(null);

        Func<Task> act = () => BaseImageManager.ColorPalette(failing, "poster", "/poster.jpg");

        await act.Should().ThrowAsync<HttpRequestException>();
    }
}
