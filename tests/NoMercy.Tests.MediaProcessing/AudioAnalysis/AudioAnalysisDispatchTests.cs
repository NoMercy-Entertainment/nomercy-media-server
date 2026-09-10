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

using Moq;
using NoMercy.Database.Models.Libraries;
using NoMercy.MediaProcessing.AudioAnalysis;
using NoMercy.MediaProcessing.Jobs.MediaJobs;
using NoMercyQueue.Core.Interfaces;
using Xunit;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

/// <summary>
/// The gate an import passes through for every track it stores. It is the only
/// layer that reaches a brand-new library at the moment its tracks appear —
/// the scan-completed hook runs before the import jobs have written any Track
/// row, so without this a first import waited for the hourly sweep.
/// </summary>
[Trait("Category", "Unit")]
public sealed class AudioAnalysisDispatchTests
{
    private static (Mock<IJobDispatcher> Dispatcher, List<IShouldQueue> Dispatched) Recorder()
    {
        List<IShouldQueue> dispatched = [];

        Mock<IJobDispatcher> dispatcher = new();
        dispatcher
            .Setup(d => d.Dispatch(It.IsAny<IShouldQueue>()))
            .Callback<IShouldQueue>(dispatched.Add);

        return (dispatcher, dispatched);
    }

    private static Library Library(bool analyzeAudio) =>
        new()
        {
            Id = Ulid.NewUlid(),
            Title = "A Library",
            Type = "music",
            AnalyzeAudio = analyzeAudio,
        };

    [Fact]
    public void AfterStore_WhenTheLibraryWantsAnalysis_QueuesThatTrack()
    {
        Guid trackId = Guid.NewGuid();
        (Mock<IJobDispatcher> dispatcher, List<IShouldQueue> dispatched) = Recorder();

        AudioAnalysisDispatch.AfterStore(dispatcher.Object, Library(analyzeAudio: true), trackId);

        IShouldQueue only = Assert.Single(dispatched);
        MusicAnalysisJob analysis = Assert.IsType<MusicAnalysisJob>(only);
        Assert.Equal(trackId, analysis.TrackId);
    }

    /// <summary>
    /// The column is the whole opt-out. A library that turned analysis off must
    /// not have its tracks analyzed by the back door of an import.
    /// </summary>
    [Fact]
    public void AfterStore_WhenTheLibraryOptedOut_QueuesNothing()
    {
        (Mock<IJobDispatcher> dispatcher, List<IShouldQueue> dispatched) = Recorder();

        AudioAnalysisDispatch.AfterStore(
            dispatcher.Object,
            Library(analyzeAudio: false),
            Guid.NewGuid()
        );

        Assert.Empty(dispatched);
    }

    /// <summary>
    /// One track, one job. A release of twelve tracks must not cost the queue
    /// twelve jobs per track.
    /// </summary>
    [Fact]
    public void AfterStore_QueuesOneJobPerTrack()
    {
        List<Guid> trackIds = [Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()];
        (Mock<IJobDispatcher> dispatcher, List<IShouldQueue> dispatched) = Recorder();
        Library library = Library(analyzeAudio: true);

        foreach (Guid trackId in trackIds)
        {
            AudioAnalysisDispatch.AfterStore(dispatcher.Object, library, trackId);
        }

        Assert.Equal(
            trackIds.Order(),
            dispatched.Cast<MusicAnalysisJob>().Select(job => job.TrackId).Order()
        );
    }
}
