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
using NoMercy.Tests.Common;

namespace NoMercy.Tests.MediaProcessing.Jobs;

/// <summary>
/// #647: a truncation failure that repeats with the same numbers is
/// deterministic; a third run only burns encode time and staging space.
/// </summary>
[Trait("Category", "Unit")]
public sealed class RepeatedTruncationFailureTests
{
    private const string Run1 =
        "Finalization failed: Encode output is truncated (2 rendition(s)): "
        + "video_1920x1080/video_1920x1080.m3u8 lasts 3117.0 s, the source lasts 3188.0 s; "
        + "video_3840x2160/video_3840x2160.m3u8 lasts 3117.0 s, the source lasts 3188.0 s";

    [Fact]
    public void SameRenditionsAndDurationsTwice_IsRepeated()
    {
        VideoEncodeJob.IsRepeatedTruncationFailure(Run1, Run1).Should().BeTrue();
    }

    [Fact]
    public void DifferentDurations_IsNotRepeated()
    {
        string run2 = Run1.Replace("lasts 3117.0 s", "lasts 2900.0 s");

        VideoEncodeJob.IsRepeatedTruncationFailure(Run1, run2).Should().BeFalse();
    }

    [Fact]
    public void DifferentRenditions_IsNotRepeated()
    {
        string run2 = Run1.Replace("video_3840x2160", "video_1280x720");

        VideoEncodeJob.IsRepeatedTruncationFailure(Run1, run2).Should().BeFalse();
    }

    [Fact]
    public void AnotherFailureKind_IsNotRepeated_EvenWithTheSameText()
    {
        VideoEncodeJob
            .IsRepeatedTruncationFailure("folder not found", "folder not found")
            .Should()
            .BeFalse();
        VideoEncodeJob.IsRepeatedTruncationFailure(null, Run1).Should().BeFalse();
        VideoEncodeJob.IsRepeatedTruncationFailure(Run1, null).Should().BeFalse();
    }

    [Fact]
    public void StopReason_SaysWhyItStoppedAndKeepsTheNumbers()
    {
        string reason = VideoEncodeJob.RepeatedTruncationStopReason(Run1);

        reason.Should().StartWith("Stopped retrying");
        reason.Should().Contain("lasts 3117.0 s, the source lasts 3188.0 s");
    }

    [Fact]
    public void StoppedRun_StillMatchesTheSameFailure()
    {
        string stopped = VideoEncodeJob.RepeatedTruncationStopReason(Run1);

        VideoEncodeJob.IsRepeatedTruncationFailure(stopped, Run1).Should().BeTrue();
    }

    [Fact]
    public void TruncationCheck_StillWritesTheTextTheJobMatchesOn()
    {
        string stage = File.ReadAllText(RepoPaths.SourceFile("FinalizeStage.cs"));

        stage.Should().Contain("$\"Encode output is truncated (");
        VideoEncodeJob.IsRepeatedTruncationFailure(Run1, Run1).Should().BeTrue();
    }

    // Source text, not behavior: the bundle runner opens its own MediaContext
    // and needs a live orchestrator and queue, so no test can drive it.
    [Fact]
    public void RepeatedFailure_GoesToFinalizeInsteadOfTheQueueRetry()
    {
        string source = File.ReadAllText(RepoPaths.SourceFile("VideoEncodeJob.cs"));
        int methodStart = source.IndexOf(
            "private async Task<bool> RunSequentialBundlesAsync",
            StringComparison.Ordinal
        );
        methodStart.Should().BeGreaterThan(0);
        string method = source[methodStart..];
        int check = method.IndexOf("IsRepeatedTruncationFailure(", StringComparison.Ordinal);
        check.Should().BeGreaterThan(0);
        int retry = method.IndexOf("throw new InvalidOperationException", StringComparison.Ordinal);
        retry.Should().BeGreaterThan(check);
        string stopBranch = method[check..retry];
        stopBranch.Should().Contain("RepeatedTruncationStopReason(");
        stopBranch.Should().Contain("Phase = CoordinatorPhase.Finalize");
        stopBranch.Should().Contain("return false;");
    }
}
