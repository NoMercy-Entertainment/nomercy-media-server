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

using System.Net;
using NoMercy.NmSystem.SystemCalls;
using Serilog.Events;

namespace NoMercy.Providers.Helpers;

public class Queue(QueueOptions options)
{
    // Two separate queues: priority drains first. Inside each, FIFO via
    // insertion-ordered Dictionary. Without this split, priority=true was
    // a no-op — Execute() walked keys in insertion order regardless of
    // the randomized uniqueId, so user-facing TMDB calls sat behind
    // background metadata fills.
    private readonly Dictionary<string, Func<Task>> _priorityTasks = [];
    private readonly Dictionary<string, Func<Task>> _tasks = [];

    private int _currentlyHandled;

    private State _state = State.Idle;
    private QueueOptions Options { get; } = options;
    private SemaphoreSlim Semaphore { get; } = new(options.Concurrent, options.Concurrent);

    private readonly object _rateLock = new();
    private DateTimeOffset _nextAllowed = DateTimeOffset.MinValue;
    private DateTimeOffset _cooldownUntil = DateTimeOffset.MinValue;

    public event EventHandler? Start;
    public event EventHandler? Stop;
    public event EventHandler? End;

    // Every _state change happens under lock (_tasks), the same lock that
    // guards the task buckets. Running therefore always means a runner that
    // has not yet passed KeepRunning's exit step, so queued work can never
    // sit behind a Running state with no runner (live 2026-10-11: the TMDB
    // queue stranded every call until restart).
    private void StartQueue()
    {
        lock (_tasks)
        {
            if (_state != State.Idle || IsEmpty)
                return;

            _state = State.Running;
        }

        Start?.Invoke(this, EventArgs.Empty);
        _ = Task.Run(RunTasksAsync, CancellationToken.None);
    }

    private void StopQueue()
    {
        lock (_tasks)
            _state = State.Stopped;

        Stop?.Invoke(this, EventArgs.Empty);
    }

    // The runner's only exit. A task enqueued before this lock is taken keeps
    // the runner going; one enqueued after it finds Idle and starts a new one.
    private bool KeepRunning()
    {
        lock (_tasks)
        {
            if (_state == State.Stopped)
                return false;

            if (!IsEmpty)
                return true;

            _state = State.Idle;
        }

        End?.Invoke(this, EventArgs.Empty);
        return false;
    }

    private async Task RunTasksAsync()
    {
        while (KeepRunning())
            try
            {
                await Dequeue();
            }
            catch (Exception ex)
            {
                Logger.App($"Queue processor error: {ex.Message}", LogEventLevel.Error);
                await Task.Delay(1000);
            }
    }

    private Task Execute()
    {
        lock (_tasks)
        {
            // Priority queue drains first, then the normal queue. Inside each,
            // insertion order = FIFO.
            DrainQueue(_priorityTasks);
            DrainQueue(_tasks);
        }

        return Task.CompletedTask;
    }

    private void DrainQueue(Dictionary<string, Func<Task>> queue)
    {
        List<string> keys = queue.Keys.ToList();
        foreach (string key in keys)
        {
            if (_currentlyHandled >= Options.Concurrent)
                return;

            if (!queue.TryGetValue(key, out Func<Task>? value))
                continue;

            _currentlyHandled++;
            queue.Remove(key);

            try
            {
                value.Invoke();
            }
            catch (Exception)
            {
                // Failures surface to callers via the per-task TaskCompletionSource.
            }
            finally
            {
                _currentlyHandled--;
            }
        }
    }

    private Task Dequeue() => Execute();

    public async Task<T> Enqueue<T>(Func<Task<T>> task, string? url, bool? priority = false)
    {
        await Semaphore.WaitAsync();

        TaskCompletionSource<T> tcs = new();

        bool isPriority = priority is true;
        string uniqueId = Ulid.NewUlid().ToString();

        lock (_tasks)
        {
            Dictionary<string, Func<Task>> bucket = isPriority ? _priorityTasks : _tasks;
            while (bucket.ContainsKey(uniqueId))
                uniqueId = Ulid.NewUlid().ToString();

            bucket.Add(
                uniqueId,
                async () =>
                {
                    try
                    {
                        int maxRetries = Options.MaxRetries;
                        for (int attempt = 0; attempt <= maxRetries; attempt++)
                        {
                            try
                            {
                                await WaitForRateSlotAsync();
                                T result = await task();
                                tcs.SetResult(result);
                                return;
                            }
                            catch (HttpRequestException ex)
                                when (attempt < maxRetries
                                    && (
                                        ex.StatusCode == HttpStatusCode.TooManyRequests
                                        || (int?)ex.StatusCode is >= 500 and <= 599
                                    )
                                )
                            {
                                TimeSpan delay = RetryDelay(ex, attempt);
                                Postpone(delay);
                                Logger.App(
                                    $"Provider {ex.StatusCode} ({url}), retrying in {delay.TotalSeconds:F1}s (attempt {attempt + 1}/{maxRetries})",
                                    LogEventLevel.Debug
                                );
                            }
                            catch (Exception ex)
                            {
                                tcs.SetException(ex);
                                if (IsExpectedTransport(ex))
                                    return;
                                Logger.App($"Url failed: {url} {ex.Message}", LogEventLevel.Debug);
                                return;
                            }
                        }
                    }
                    finally
                    {
                        Semaphore.Release();
                        lock (_tasks)
                        {
                            _priorityTasks.Remove(uniqueId);
                            _tasks.Remove(uniqueId);
                        }
                    }
                }
            );
        }

        if (Options.Start)
            StartQueue();

        return await tcs.Task;
    }

    private async Task WaitForRateSlotAsync()
    {
        DateTimeOffset slot;
        lock (_rateLock)
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            slot = now > _nextAllowed ? now : _nextAllowed;
            _nextAllowed = slot.AddMilliseconds(Options.Interval);
        }

        while (true)
        {
            TimeSpan wait = slot - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
                await Task.Delay(wait);

            lock (_rateLock)
            {
                if (DateTimeOffset.UtcNow >= _cooldownUntil)
                    return;

                slot = _cooldownUntil > _nextAllowed ? _cooldownUntil : _nextAllowed;
                _nextAllowed = slot.AddMilliseconds(Options.Interval);
            }
        }
    }

    private void Postpone(TimeSpan delay)
    {
        lock (_rateLock)
        {
            DateTimeOffset until = DateTimeOffset.UtcNow.Add(delay);
            if (until > _cooldownUntil)
                _cooldownUntil = until;
            if (until > _nextAllowed)
                _nextAllowed = until;
        }
    }

    private TimeSpan RetryDelay(HttpRequestException exception, int attempt)
    {
        int exponential = Math.Min(30_000, Options.RetryBaseDelayMs * (1 << attempt));
        int jitter = Random.Shared.Next(0, Math.Max(1, exponential / 5));
        TimeSpan delay = TimeSpan.FromMilliseconds(exponential + jitter);
        if (exception.Data["RetryAfter"] is TimeSpan retryAfter && retryAfter > TimeSpan.Zero)
            delay = retryAfter;
        return delay;
    }

    private void Clear()
    {
        lock (_tasks)
        {
            _priorityTasks.Clear();
            _tasks.Clear();
        }
    }

    private int Size
    {
        get
        {
            lock (_tasks)
            {
                return _priorityTasks.Count + _tasks.Count;
            }
        }
    }

    private bool IsEmpty => Size == 0;

    private static bool IsExpectedTransport(Exception ex)
    {
        return ex
            is HttpRequestException
            {
                StatusCode: HttpStatusCode.NotFound
                    or HttpStatusCode.BadGateway
                    or HttpStatusCode.ServiceUnavailable
                    or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.TooManyRequests,
            };
    }
}
