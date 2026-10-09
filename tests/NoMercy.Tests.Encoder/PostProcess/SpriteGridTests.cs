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

using NoMercy.Encoder.PostProcess;

namespace NoMercy.Tests.Encoder.PostProcess;

/// <summary>
/// The grid exists to leave the muxer no empty cell to render green, so the one
/// property that matters is that it is exactly fillable and never smaller than
/// the film needs.
/// </summary>
public class SpriteGridTests
{
    [Theory]
    [InlineData(75)] // 8 real frames at a 10s interval
    [InlineData(80)] // boundary: the frame at 80s does not exist
    [InlineData(1450)] // a 24-minute episode
    [InlineData(7200)] // a two-hour film
    [InlineData(10800)] // a three-hour film
    public void CellCount_IsExactlyTheGrid(int durationSeconds)
    {
        SpriteGrid grid = SpriteGrid.For(TimeSpan.FromSeconds(durationSeconds), 10);

        grid.CellCount.Should()
            .Be(
                grid.Columns * grid.Rows,
                "a partially filled row is the thing that comes out green"
            );
    }

    [Theory]
    [InlineData(75)]
    [InlineData(80)]
    [InlineData(1450)]
    [InlineData(7200)]
    public void CellCount_LeavesRoomForEveryRealFrame(int durationSeconds)
    {
        // The stream is cut at CellCount, so a grid smaller than the film needs
        // would drop real thumbnails off the end rather than pad it.
        int mostFramesPossible = durationSeconds / 10 + 1;

        SpriteGrid
            .For(TimeSpan.FromSeconds(durationSeconds), 10)
            .CellCount.Should()
            .BeGreaterThanOrEqualTo(
                mostFramesPossible,
                "over-estimating costs a black tile, under-estimating costs film"
            );
    }

    [Fact]
    public void Grid_StaysRoughlySquare()
    {
        // A sheet is decoded as one image on the clients, so a wildly lopsided
        // grid is a real cost — 720 frames must not become a single 720-wide row.
        SpriteGrid grid = SpriteGrid.For(TimeSpan.FromSeconds(7200), 10);

        grid.Columns.Should().BeInRange(20, 35);
        grid.Rows.Should().BeInRange(20, 35);
    }

    [Fact]
    public void AZeroLengthTitle_StillHasACell()
    {
        SpriteGrid grid = SpriteGrid.For(TimeSpan.Zero, 10);

        grid.Columns.Should().BeGreaterThan(0);
        grid.Rows.Should().BeGreaterThan(0);
    }

    [Fact]
    public void AnAbsurdIntervalDoesNotDivideByZero()
    {
        SpriteGrid.For(TimeSpan.FromSeconds(600), 0).CellCount.Should().BeGreaterThan(0);
    }
}
