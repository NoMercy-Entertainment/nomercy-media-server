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
using NoMercy.Api.Controllers.V1.Media;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Media;
using Xunit;

namespace NoMercy.Tests.Api;

[Trait("Category", "ContentSegmentEditValidation")]
public class ContentSegmentEditValidationTests
{
    [Theory]
    [InlineData(100.0, 50.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(-1.0, 50.0)]
    [InlineData(0.0, -1.0)]
    public async Task EncoderEdit_RejectsInvalidTimesWithoutWriting(double start, double end)
    {
        Mock<IContentSegmentRepository> repository = new();
        EncoderContentAnalysisController controller = new(
            null!,
            null,
            null,
            null!,
            repository.Object,
            null!
        );

        IActionResult result = await controller.EditSegment(
            Ulid.NewUlid().ToString(),
            new EditSegmentRequest(start, end)
        );

        ObjectResult response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, response.StatusCode);
        repository.Verify(
            r => r.UpdateAsync(It.IsAny<Ulid>(), It.IsAny<Action<ContentSegment>>()),
            Times.Never
        );
    }

    [Theory]
    [InlineData(100.0, 50.0)]
    [InlineData(100.0, 100.0)]
    [InlineData(-1.0, 50.0)]
    [InlineData(0.0, -1.0)]
    [InlineData(90.0, null)]
    [InlineData(null, 5.0)]
    public async Task MediaEdit_RejectsInvalidMergedTimesWithoutWriting(double? start, double? end)
    {
        Ulid id = Ulid.NewUlid();
        Mock<IContentSegmentRepository> repository = new();
        repository
            .Setup(r => r.GetByIdAsync(id))
            .ReturnsAsync(
                new ContentSegment
                {
                    Id = id,
                    StartSeconds = 10,
                    EndSeconds = 50,
                }
            );
        ContentSegmentsController controller = new(repository.Object);

        IActionResult result = await controller.Update(
            id.ToString(),
            new UpdateContentSegmentRequest(StartSeconds: start, EndSeconds: end)
        );

        ObjectResult response = Assert.IsType<ObjectResult>(result);
        Assert.Equal(400, response.StatusCode);
        repository.Verify(
            r => r.UpdateAsync(It.IsAny<Ulid>(), It.IsAny<Action<ContentSegment>>()),
            Times.Never
        );
    }
}
