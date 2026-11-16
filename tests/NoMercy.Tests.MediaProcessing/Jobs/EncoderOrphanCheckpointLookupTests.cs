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

using Microsoft.Extensions.Logging.Abstractions;
using NoMercy.Encoder.Jobs;
using NoMercy.Encoder.Orchestration;
using NoMercy.MediaProcessing.Jobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercy.MediaProcessing.Jobs.MediaJobs.Support;
using NoMercyQueue;

namespace NoMercy.Tests.MediaProcessing.Jobs;

public class EncoderOrphanCheckpointLookupTests
{
    private const string OutputDirectory = "Made.Up.Show.(2000)/Made.Up.Show.S01E01";

    private sealed class DirectoryKeyedCheckpointStore : ICheckpointStore
    {
        private readonly Dictionary<string, JobCheckpoint> _checkpoints = [];

        public Task SaveAsync(JobCheckpoint checkpoint, CancellationToken ct = default)
        {
            _checkpoints[checkpoint.OutputDirectory] = checkpoint;
            return Task.CompletedTask;
        }

        public Task<JobCheckpoint?> LoadAsync(
            string outputDirectory,
            CancellationToken ct = default
        ) => Task.FromResult(_checkpoints.GetValueOrDefault(outputDirectory));

        public Task DeleteAsync(string outputDirectory, CancellationToken ct = default)
        {
            _checkpoints.Remove(outputDirectory);
            return Task.CompletedTask;
        }
    }

    private static string CoordinatorPayload(string outputDirectory)
    {
        VideoEncodeJob job = new()
        {
            Coordinator = new(
                GroupTag: "group",
                TaskIds: [],
                Phase: CoordinatorPhase.WaitChildren,
                Pass1DispatchedAt: null,
                Pass2DispatchedAt: null,
                Pass1StatsPath: null,
                PresetId: Ulid.NewUlid(),
                ExpectedFinalCount: 1,
                OutputDirectory: outputDirectory
            ),
        };
        return SerializationHelper.Serialize(job);
    }

    // The execute stage saves under the folder the orchestrator hands it: the
    // mirrored working folder of the job's storage-relative output directory.
    private static async Task SaveCrashCheckpointAsExecuteStageDoesAsync(ICheckpointStore store)
    {
        string workingDirectory = EncoderTempDirectory.Resolve(OutputDirectory);
        await store.SaveAsync(
            new(
                JobId: "job",
                InputPath: "/media/made-up.mkv",
                OutputDirectory: workingDirectory,
                CompletedGroupIndices: [],
                LastUpdated: DateTime.UtcNow,
                LastProgressMs: 60_000,
                FailedAt: DateTime.UtcNow
            )
        );
    }

    [Fact]
    public async Task HasCheckpoint_CrashCheckpointInTheWorkingFolder_IsFound()
    {
        DirectoryKeyedCheckpointStore store = new();
        await SaveCrashCheckpointAsExecuteStageDoesAsync(store);
        EncoderOrphanCheckpointLookup lookup = new(
            store,
            NullLogger<EncoderOrphanCheckpointLookup>.Instance
        );

        bool found = await lookup.HasCheckpointAsync(CoordinatorPayload(OutputDirectory));

        found.Should().BeTrue();
    }

    [Fact]
    public async Task HasCheckpoint_CheckpointOfAnotherJob_IsNotFound()
    {
        DirectoryKeyedCheckpointStore store = new();
        await SaveCrashCheckpointAsExecuteStageDoesAsync(store);
        EncoderOrphanCheckpointLookup lookup = new(
            store,
            NullLogger<EncoderOrphanCheckpointLookup>.Instance
        );

        bool found = await lookup.HasCheckpointAsync(
            CoordinatorPayload("Other.Show.(2001)/Other.Show.S01E01")
        );

        found.Should().BeFalse();
    }

    [Fact]
    public async Task HasCheckpoint_JobThatHasNotStarted_IsNotFound()
    {
        DirectoryKeyedCheckpointStore store = new();
        await SaveCrashCheckpointAsExecuteStageDoesAsync(store);
        EncoderOrphanCheckpointLookup lookup = new(
            store,
            NullLogger<EncoderOrphanCheckpointLookup>.Instance
        );

        bool found = await lookup.HasCheckpointAsync(
            SerializationHelper.Serialize(new VideoEncodeJob())
        );

        found.Should().BeFalse();
    }
}
