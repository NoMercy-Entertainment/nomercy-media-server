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

using Microsoft.EntityFrameworkCore;
using Moq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NoMercy.Api.DTOs.Media;
using NoMercy.Api.Services;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Media;
using Xunit;

namespace NoMercy.Tests.Api.Media;

public class ScreensaverPaletteTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task GetSetupScreensaverContent_SendsLogoPaletteOrNull(bool hasLogoPalette)
    {
        Image backdrop = new()
        {
            TvId = 7,
            Type = "backdrop",
            FilePath = "/backdrop.jpg",
            ColorPalette = new() { Image = new() { Dominant = "#123456" } },
        };
        Image logo = new()
        {
            TvId = 7,
            Type = "logo",
            FilePath = "/logo.png",
        };
        if (hasLogoPalette)
            logo.ColorPalette = new() { Image = new() { Dominant = "#ffffff" } };

        Mock<IHomeRepository> repository = new();
        repository
            .Setup(r =>
                r.GetScreensaverImagesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>())
            )
            .ReturnsAsync([backdrop, logo]);
        HomeService service = new(
            repository.Object,
            Mock.Of<ILibraryRepository>(),
            Mock.Of<IDbContextFactory<MediaContext>>()
        );

        ScreensaverDto result = await service.GetSetupScreensaverContent(Guid.Empty);
        JObject json = JObject.Parse(JsonConvert.SerializeObject(result));
        JToken? item = json["data"]?.Single();

        Assert.Equal("#123456", (string?)item?["color_palette"]?["image"]?["dominant"]);
        JObject logoJson = Assert.IsType<JObject>(item?["meta"]?["logo"]);
        Assert.True(logoJson.ContainsKey("color_palette"));
        if (hasLogoPalette)
            Assert.Equal("#ffffff", (string?)logoJson["color_palette"]?["image"]?["dominant"]);
        else
            Assert.Equal(JTokenType.Null, logoJson["color_palette"]?.Type);
    }
}
