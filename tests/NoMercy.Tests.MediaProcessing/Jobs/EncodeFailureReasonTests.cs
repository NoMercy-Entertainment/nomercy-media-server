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
using FluentAssertions;
using NoMercy.MediaProcessing.Jobs.MediaJobs;

namespace NoMercy.Tests.MediaProcessing.Jobs;

public class EncodeFailureReasonTests
{
    [Fact]
    public void Several_failed_steps_read_as_a_plain_sentence()
    {
        string reason = VideoEncodeJob.PlainFailureReason(2, ["Video (2x)"]);

        reason.Should().Be("The encode did not finish: 2 steps failed (Video (2x)). You can retry it.");
    }

    [Fact]
    public void One_failed_step_uses_the_singular()
    {
        string reason = VideoEncodeJob.PlainFailureReason(1, ["Audio"]);

        reason.Should().Be("The encode did not finish: 1 step failed (Audio). You can retry it.");
    }

    [Fact]
    public void The_old_rung_wording_is_gone()
    {
        VideoEncodeJob.PlainFailureReason(3, []).Should().NotContain("rung");
    }

    [Fact]
    public void Without_a_step_list_the_sentence_still_stands_alone()
    {
        VideoEncodeJob
            .PlainFailureReason(3, [])
            .Should()
            .Be("The encode did not finish: 3 steps failed. You can retry it.");
    }
}
