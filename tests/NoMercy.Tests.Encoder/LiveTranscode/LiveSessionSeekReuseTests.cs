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

using NoMercy.Encoder.Codecs;
using NoMercy.Encoder.LiveTranscode;

namespace NoMercy.Tests.Encoder.LiveTranscode;

/// <summary>
/// The app seeks to its resume point right after a session starts. When the
/// running FFmpeg already starts there, a respawn only throws away its warm-up
/// (2026-10-14: every live session launched FFmpeg twice, 180 ms apart).
/// </summary>
public class LiveSessionSeekReuseTests
{
    private static LiveQuality MakeQuality() =>
        new(
            Id: "1080p",
            Label: "1080p",
            Width: 1920,
            Height: 1080,
            Codec: VideoCodecType.H264,
            BitrateKbps: 8000,
            Encoder: "libx264",
            IsHardwareAccelerated: false,
            ExpectedSpeed: 2.0,
            CanRealtime: true
        );

    private static (LiveSession Session, Func<int> Spawns) StartedAt(TimeSpan from)
    {
        LiveSession session = new("reuse-001", MakeQuality());
        int spawns = 0;
        session.AttachRunnerFactory(
            (_, _) =>
            {
                Interlocked.Increment(ref spawns);
                return Task.CompletedTask;
            }
        );
        session.MarkTranscodeStart(from);
        return (session, () => Volatile.Read(ref spawns));
    }

    [Fact]
    public async Task SeekAsync_ToWhereTheRunnerStarted_KeepsTheRunner()
    {
        (LiveSession session, Func<int> spawns) = StartedAt(TimeSpan.FromSeconds(300));
        CancellationToken runner = session.RunnerCancellation;

        await session.SeekAsync(TimeSpan.FromSeconds(300), CancellationToken.None);
        await Task.Delay(100);

        spawns().Should().Be(0);
        runner.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task SeekAsync_FarAhead_RespawnsTheRunner()
    {
        (LiveSession session, Func<int> spawns) = StartedAt(TimeSpan.FromSeconds(300));
        CancellationToken runner = session.RunnerCancellation;

        await session.SeekAsync(TimeSpan.FromSeconds(900), CancellationToken.None);
        await Task.Delay(100);

        spawns().Should().Be(1);
        runner.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task SeekAsync_BeforeTheRunnerStart_RespawnsTheRunner()
    {
        (LiveSession session, Func<int> spawns) = StartedAt(TimeSpan.FromSeconds(300));

        await session.SeekAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        await Task.Delay(100);

        spawns().Should().Be(1);
    }
}
