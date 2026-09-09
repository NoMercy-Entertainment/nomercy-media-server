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
using NoMercy.Database;
using NoMercy.Events;
using NoMercy.Events.Library;
using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Service.Subscribers;

/// <summary>
/// Queues audio analysis for a music library the moment its scan finishes, so
/// analysis is part of every import and rescan rather than something a user has
/// to go and find.
/// <para>
/// Only the trigger lives here. Which tracks still need a verdict is
/// <see cref="IAudioAnalysisScheduler" />'s question, and the hourly
/// <see cref="Jobs.AudioAnalysisSweepCronJob" /> asks the same one for the
/// library a user already had.
/// </para>
/// <para>
/// The opt-out is <c>Library.AnalyzeAudio</c>, which is on by default for music
/// libraries.
/// </para>
/// </summary>
public class AudioAnalysisSubscriber(
    IEventBus eventBus,
    IAudioAnalysisScheduler scheduler,
    IDbContextFactory<MediaContext> contextFactory,
    ILogger<AudioAnalysisSubscriber> logger
) : IHostedService
{
    private const string MusicLibraryType = "music";

    private readonly List<IDisposable> _subscriptions = [];

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _subscriptions.Add(eventBus.Subscribe<LibraryScanCompletedEvent>(OnLibraryScanCompleted));

        logger.LogInformation("Audio analysis subscriber active");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        foreach (IDisposable subscription in _subscriptions)
        {
            try
            {
                subscription.Dispose();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Could not dispose the audio analysis subscription");
            }
        }

        _subscriptions.Clear();

        return Task.CompletedTask;
    }

    internal async Task OnLibraryScanCompleted(
        LibraryScanCompletedEvent @event,
        CancellationToken cancellationToken
    )
    {
        try
        {
            await using MediaContext mediaContext = await contextFactory.CreateDbContextAsync(
                cancellationToken
            );

            bool wantsAnalysis = await mediaContext
                .Libraries.AsNoTracking()
                .AnyAsync(
                    library =>
                        library.Id == @event.LibraryId
                        && library.Type == MusicLibraryType
                        && library.AnalyzeAudio,
                    cancellationToken
                );

            if (!wantsAnalysis)
            {
                return;
            }

            int queued = await scheduler.QueueAsync([@event.LibraryId], cancellationToken);

            logger.LogInformation(
                "Audio analysis queued {Queued} track(s) after the scan of {LibraryName}",
                [queued, @event.LibraryName]
            );
        }
        catch (Exception ex)
        {
            // A scan that finished is a success the user can see. Losing the
            // analysis that should follow it is a smaller failure than losing
            // the scan, so it stays inside this handler.
            logger.LogError(
                ex,
                "Audio analysis could not be queued after the scan of {LibraryName}",
                @event.LibraryName
            );
        }
    }
}
