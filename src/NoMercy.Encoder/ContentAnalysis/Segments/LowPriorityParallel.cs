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

namespace NoMercy.Encoder.ContentAnalysis.Segments;

/// <summary>
/// Runs CPU-bound work items on a few dedicated below-normal-priority threads,
/// so the season matching never competes with playback, encodes or the web
/// host for the CPU, and the caller awaits it instead of blocking a queue
/// worker. Between items each thread pauses while <c>paused</c> says so.
/// </summary>
internal static class LowPriorityParallel
{
    private static readonly TimeSpan PausePoll = TimeSpan.FromSeconds(1);

    public static Task ForAsync(
        int count,
        int parallelism,
        Action<int> body,
        Func<bool>? paused,
        CancellationToken ct
    )
    {
        if (count == 0)
            return Task.CompletedTask;

        TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int next = -1;
        int threads = Math.Clamp(parallelism, 1, count);
        int running = threads;
        Exception? failure = null;

        for (int t = 0; t < threads; t++)
        {
            Thread thread = new(() =>
            {
                try
                {
                    while (!ct.IsCancellationRequested && Volatile.Read(ref failure) is null)
                    {
                        while (paused?.Invoke() == true && !ct.IsCancellationRequested)
                            Thread.Sleep(PausePoll);

                        int item = Interlocked.Increment(ref next);
                        if (item >= count || ct.IsCancellationRequested)
                            break;
                        body(item);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                }
                finally
                {
                    if (Interlocked.Decrement(ref running) == 0)
                    {
                        if (failure is not null)
                            done.TrySetException(failure);
                        else if (ct.IsCancellationRequested)
                            done.TrySetCanceled(ct);
                        else
                            done.TrySetResult();
                    }
                }
            })
            {
                IsBackground = true,
                Priority = ThreadPriority.BelowNormal,
                Name = "segment-matching",
            };
            thread.Start();
        }

        return done.Task;
    }
}
