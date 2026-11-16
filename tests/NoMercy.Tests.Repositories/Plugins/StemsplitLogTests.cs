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
/// The stemsplit filter's lines as the host reads them, on lines recorded
/// from the real filter (<see cref="StemsplitLogFixtures" />).
/// </summary>
public class StemsplitLogTests
{
    [Theory]
    [InlineData(StemsplitLogFixtures.NoGpuRefusal)]
    [InlineData(StemsplitLogFixtures.MemoryRefusal)]
    [InlineData(StemsplitLogFixtures.PartialMemoryRefusal)]
    [InlineData(StemsplitLogFixtures.UnknownOption)]
    public void BothRefusals_AndABuildWithoutTheOption_AreRefusals(string line)
    {
        StemsplitLog.IsRefusal(line).Should().BeTrue();
    }

    [Theory]
    [InlineData(StemsplitLogFixtures.UseGpuMistake)]
    [InlineData(StemsplitLogFixtures.RunsOn)]
    [InlineData(StemsplitLogFixtures.FirstChunk)]
    [InlineData(StemsplitLogFixtures.SpleeterLoaded)]
    [InlineData(StemsplitLogFixtures.Banner)]
    public void AnyOtherLine_IsNoRefusal(string line)
    {
        StemsplitLog.IsRefusal(line).Should().BeFalse();
    }

    [Fact]
    public void NoGpuAtAll_RefusesEveryPreset_AMemoryRefusalOnlyItsRun()
    {
        StemsplitLog.RefusesEveryPreset(StemsplitLogFixtures.NoGpuRefusal).Should().BeTrue();
        StemsplitLog.RefusesEveryPreset(StemsplitLogFixtures.UnknownOption).Should().BeTrue();
        StemsplitLog.RefusesEveryPreset(StemsplitLogFixtures.MemoryRefusal).Should().BeFalse();
    }

    [Fact]
    public void AMemoryRefusal_NamesTheRunsModelCount()
    {
        StemsplitLog.RefusedRunModels(StemsplitLogFixtures.PartialMemoryRefusal).Should().Be(2);
        StemsplitLog.RefusedRunModels(StemsplitLogFixtures.NoGpuRefusal).Should().BeNull();
    }

    [Fact]
    public void AChunkLine_IsRead_WithAnUnknownTotal()
    {
        StemsplitLog
            .Chunk(StemsplitLogFixtures.FirstChunk)
            .Should()
            .Be(new StemsplitChunk(1, 2, 1, null, "Parsed_stemsplit_0"));
    }

    [Fact]
    public void AChunkLine_IsFound_BehindAProgressReport()
    {
        StemsplitLog
            .Chunk(StemsplitLogFixtures.ChunkAfterStats)
            .Should()
            .Be(new StemsplitChunk(2, 2, 2, null, "Parsed_stemsplit_0"));
    }

    [Fact]
    public void TwoFiltersInOneGraph_AreToldApartByTheirInstanceNames()
    {
        StemsplitLog
            .Chunk(StemsplitLogFixtures.TwoFilters[7])!
            .Filter.Should()
            .Be("Parsed_stemsplit_1");
        StemsplitLog
            .Chunk(StemsplitLogFixtures.TwoFilters[9])!
            .Filter.Should()
            .Be("Parsed_stemsplit_2");
    }

    [Fact]
    public void AChunkLineWithoutAnInstanceName_HasNoFilter()
    {
        StemsplitLog
            .Chunk(
                "stemsplit: model 1/1, chunk 3/9 at sample 0 (485100 samples, 485376 received), 3.0 s elapsed, rtf 1.00."
            )
            .Should()
            .Be(new StemsplitChunk(1, 1, 3, 9));
    }

    [Fact]
    public void OnlyTheLastChunkOfTheLastModel_IsTheLast()
    {
        StemsplitLog.Chunk(StemsplitLogFixtures.FirstChunk)!.IsLast.Should().BeFalse();
        StemsplitLog.Chunk(StemsplitLogFixtures.LastChunkOfFirstModel)!.IsLast.Should().BeFalse();
        StemsplitLog.Chunk(StemsplitLogFixtures.LastChunk)!.IsLast.Should().BeTrue();
    }

    [Theory]
    [InlineData(StemsplitLogFixtures.ModelLoaded)]
    [InlineData(StemsplitLogFixtures.SpleeterLoaded)]
    [InlineData(StemsplitLogFixtures.RunsOn)]
    public void LinesThatAreNoChunk_AreNotRead(string line)
    {
        StemsplitLog.Chunk(line).Should().BeNull();
    }

    [Fact]
    public void TheDevice_IsReadFromTheRunsOnLine()
    {
        StemsplitLog
            .RoformerDevice(StemsplitLogFixtures.RunsOn)
            .Should()
            .Be("vulkan (NVIDIA GeForce RTX 3070)");
        StemsplitLog.RoformerDevice(StemsplitLogFixtures.FirstChunk).Should().BeNull();
    }
}
