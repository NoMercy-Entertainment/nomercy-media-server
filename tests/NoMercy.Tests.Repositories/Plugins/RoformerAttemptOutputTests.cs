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
using NoMercy.Data.Plugins;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The plugin's callbacks for a run the filter may still refuse: lines held
/// back until the first chunk, dropped after a refusal, and always handed on
/// in the order they came - without the plugin's callback running under the
/// lock both readers share.
/// </summary>
public class RoformerAttemptOutputTests
{
    [Fact]
    public void HeldLines_AreHandedOnAtTheFirstChunk_InTheirOrder()
    {
        List<string> seen = [];
        RoformerAttemptOutput output = new(
            line => seen.Add("out " + line),
            line => seen.Add("err " + line),
            holdBack: true
        );

        output.StdErr(StemsplitLogFixtures.Banner);
        output.StdOut("{\"header\":1}");
        seen.Should().BeEmpty();

        output.StdErr(StemsplitLogFixtures.FirstChunk);
        output.StdOut("{\"line\":1}");

        seen.Should()
            .Equal(
                "err " + StemsplitLogFixtures.Banner,
                "out {\"header\":1}",
                "err " + StemsplitLogFixtures.FirstChunk,
                "out {\"line\":1}"
            );
    }

    [Fact]
    public void ARefusedRun_HandsNothingOn_AndSaysWhy()
    {
        List<string> seen = [];
        RoformerAttemptOutput output = new(seen.Add, seen.Add, holdBack: true);

        output.StdErr(StemsplitLogFixtures.Banner);
        output.StdErr(StemsplitLogFixtures.NoGpuRefusal);

        seen.Should().BeEmpty();
        output.Refusal.Should().Be(StemsplitLogFixtures.NoGpuRefusal);
    }

    /// <summary>
    /// A slow plugin callback on one reader thread must not hold the other
    /// reader inside the lock: its line is queued, and handed on after the
    /// slow one, in order.
    /// </summary>
    [Fact]
    public async Task ASlowCallback_DoesNotBlockTheOtherReader_AndOrderIsKept()
    {
        using ManualResetEventSlim release = new();
        using ManualResetEventSlim inCallback = new();
        List<string> seen = [];
        RoformerAttemptOutput output = new(
            line =>
            {
                inCallback.Set();
                release.Wait(TimeSpan.FromSeconds(5));
                lock (seen)
                    seen.Add(line);
            },
            line =>
            {
                lock (seen)
                    seen.Add(line);
            },
            holdBack: false
        );

        Task stdOutReader = Task.Run(() => output.StdOut("first"));
        inCallback.Wait(TimeSpan.FromSeconds(5)).Should().BeTrue();

        Task stdErrReader = Task.Run(() => output.StdErr("second"));
        (await Task.WhenAny(stdErrReader, Task.Delay(TimeSpan.FromSeconds(2))))
            .Should()
            .BeSameAs(stdErrReader, "the other reader only queues its line");

        release.Set();
        await stdOutReader;

        seen.Should().Equal("first", "second");
    }
}
