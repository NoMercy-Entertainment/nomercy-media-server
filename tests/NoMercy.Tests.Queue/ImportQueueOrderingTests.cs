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

using NoMercy.Database;
using NoMercy.Tests.Queue.TestHelpers;
using NoMercyQueue.Core.Models;
using Xunit;

namespace NoMercy.Tests.Queue;

[Trait("Category", "Unit")]
public class ImportQueueOrderingTests
{
    // Priorities mirror AudioImportJob (4) and MovieImportJob (5).
    private const int AudioPriority = 4;
    private const int MoviePriority = 5;

    [Fact]
    public void Movie_job_enqueued_after_many_audio_jobs_is_reserved_first()
    {
        (QueueContext context, NoMercyQueue.Core.Interfaces.IQueueContext adapter) =
            TestQueueContextFactory.CreateInMemoryContextWithAdapter();
        using (context)
        using (adapter)
        {
            for (int i = 0; i < 50; i++)
            {
                adapter.AddJob(
                    new QueueJobModel
                    {
                        Payload = $"{{\"audio\":{i}}}",
                        Queue = "import",
                        Priority = AudioPriority,
                        AvailableAt = DateTime.UtcNow,
                    }
                );
            }

            adapter.AddJob(
                new QueueJobModel
                {
                    Payload = "{\"movie\":true}",
                    Queue = "import",
                    Priority = MoviePriority,
                    AvailableAt = DateTime.UtcNow,
                }
            );

            QueueJobModel? next = adapter.GetNextJob("import", 3, null, DateTime.UtcNow);

            Assert.NotNull(next);
            Assert.Equal("{\"movie\":true}", next.Payload);
        }
    }
}
