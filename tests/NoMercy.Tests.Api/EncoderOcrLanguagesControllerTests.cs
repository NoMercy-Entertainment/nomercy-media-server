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

using Microsoft.AspNetCore.Mvc;
using Moq;
using NoMercy.Api.Controllers.V1.Encoder;
using NoMercy.Encoder.Subtitles;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "Unit")]
public class EncoderOcrLanguagesControllerTests
{
    [Fact]
    public async Task GetLanguages_ReleaseUnreachable_FallsBackToDownloadedLanguages()
    {
        Mock<ITesseractModelManager> manager = new();
        manager
            .Setup(m => m.GetAvailableLanguagesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Could not reach the release"));
        manager.Setup(m => m.GetDownloadedLanguages()).Returns(["eng"]);
        EncoderOcrLanguagesController controller = new(manager.Object);

        IActionResult result = await controller.GetLanguages(CancellationToken.None);

        OkObjectResult ok = Assert.IsType<OkObjectResult>(result);
        object body = ok.Value!;
        Assert.Equal(
            ["eng"],
            (IReadOnlyList<string>)body.GetType().GetProperty("available")!.GetValue(body)!
        );
        Assert.Equal(
            ["eng"],
            (IReadOnlyList<string>)body.GetType().GetProperty("downloaded")!.GetValue(body)!
        );
    }

    [Fact]
    public async Task GetLanguages_Cancelled_StillThrowsCancellation()
    {
        Mock<ITesseractModelManager> manager = new();
        manager
            .Setup(m => m.GetAvailableLanguagesAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new OperationCanceledException());
        EncoderOcrLanguagesController controller = new(manager.Object);
        using CancellationTokenSource cts = new();
        await cts.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            controller.GetLanguages(cts.Token)
        );
    }
}
