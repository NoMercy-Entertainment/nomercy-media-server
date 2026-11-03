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

using NoMercy.MediaProcessing.Jobs.MediaJobs;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.Jobs;

[Trait("Category", "Unit")]
public class ImportQueuePriorityTests
{
    [Fact]
    public void AudioImportJob_priority_is_below_every_video_and_person_import()
    {
        int audio = new AudioImportJob().Priority;

        Assert.True(audio < new MovieImportJob().Priority);
        Assert.True(audio < new ShowImportJob().Priority);
        Assert.True(audio < new PersonRefreshJob().Priority);
    }
}
