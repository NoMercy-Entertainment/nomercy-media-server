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

using NoMercy.Api.DTOs.Management;
using NoMercy.Api.Services;
using Xunit;

namespace NoMercy.Tests.Api.Services;

[Trait("Category", "Unit")]
public sealed class ServerWorkloadReaderTests
{
    [Fact]
    public void From_CountsStreamsAndEncoderWorkers()
    {
        ManagementActivityDto activity = ServerWorkloadReader.From(
            2,
            ["encoder-0", "Encoder-1", "library-0"]
        );

        activity
            .Should()
            .Be(
                new ManagementActivityDto
                {
                    ActiveStreams = 2,
                    ActiveEncodes = 2,
                    CanInterruptSafely = false,
                }
            );
    }

    [Fact]
    public void From_WithoutStreams_IsSafeToInterrupt()
    {
        ManagementActivityDto activity = ServerWorkloadReader.From(0, ["encoder-0"]);

        activity.CanInterruptSafely.Should().BeTrue();
        activity.ActiveEncodes.Should().Be(1);
    }
}
