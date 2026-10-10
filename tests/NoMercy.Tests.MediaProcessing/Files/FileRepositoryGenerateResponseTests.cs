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

using System.Collections.Concurrent;
using NoMercy.MediaProcessing.Files;
using NoMercy.NmSystem;
using NoMercy.NmSystem.Dto;
using NoMercy.Providers.MusicBrainz.Models;

namespace NoMercy.Tests.MediaProcessing.Files;

[Trait("Category", "Unit")]
public class FileRepositoryGenerateResponseTests
{
    [Fact]
    public async Task Parallel_candidates_are_returned_once_per_release()
    {
        int parallelCandidates = Math.Min(16, SystemParallelism.Options.MaxDegreeOfParallelism);
        Guid repeatedId = Guid.NewGuid();
        List<MusicBrainzReleaseAppends> releases =
        [
            .. Enumerable
                .Range(0, parallelCandidates)
                .Select(_ => new MusicBrainzReleaseAppends
                {
                    Id = repeatedId,
                    Title = "Repeated pressing",
                }),
            .. Enumerable
                .Range(0, 128)
                .Select(_ => new MusicBrainzReleaseAppends
                {
                    Id = Guid.NewGuid(),
                    Title = "Other pressing",
                }),
        ];
        TaskCompletionSource barrier = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int waiting = 0;

        async Task<Uri?> GetCoverUrl(Guid _)
        {
            if (Interlocked.Increment(ref waiting) <= parallelCandidates)
            {
                if (Volatile.Read(ref waiting) == parallelCandidates)
                    barrier.SetResult();
                await barrier.Task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            return null;
        }

        for (int run = 0; run < 5; run++)
        {
            List<FileItem> result = await FileRepository.GenerateResponse(
                "/music/album",
                releases,
                new ConcurrentBag<MediaFile>(),
                "2026",
                GetCoverUrl
            );

            result.Select(item => item.Match.Id).Should().OnlyHaveUniqueItems();
            result.Should().HaveCount(releases.Select(release => release.Id).Distinct().Count());
        }
    }
}
