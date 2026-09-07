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

using NoMercy.MediaProcessing.AudioAnalysis;

namespace NoMercy.Tests.MediaProcessing.AudioAnalysis;

/// <summary>
/// Energy is a formula, not a measurement, so it is pinned to numbers rather
/// than to itself. Asserting <c>Estimate(x) == Estimate(x)</c> passes for every
/// possible formula and catches no change at all.
/// <para>
/// Loudness maps -30..-5 LUFS to 0..1 and carries 0.65; brightness maps
/// 500..5000 Hz to 0..1 and carries 0.35. Changing any of those five constants
/// changes the meaning of a stored column and must fail here first.
/// </para>
/// </summary>
public class AudioEnergyTests
{
    [Fact]
    public void Estimate_CombinesLoudnessAndBrightnessInTheDocumentedRatio()
    {
        // loudness (-9 + 30) / 25 = 0.84, brightness (2400 - 500) / 4500 = 0.4222
        // 0.65 * 0.84 + 0.35 * 0.42222 = 0.693778
        AudioEnergy.Estimate(-9.0, 2400.0).Should().BeApproximately(0.693778, 0.000001);
    }

    [Fact]
    public void Estimate_ScoresTheQuietDarkEndZero()
    {
        AudioEnergy.Estimate(-30.0, 500.0).Should().Be(0.0);
    }

    [Fact]
    public void Estimate_ScoresTheLoudBrightEndOne()
    {
        AudioEnergy.Estimate(-5.0, 5000.0).Should().Be(1.0);
    }

    [Theory]
    [InlineData(-60.0, 20.0, 0.0)]
    [InlineData(0.0, 20000.0, 1.0)]
    public void Estimate_ClampsBeyondTheDocumentedRange(
        double lufs,
        double centroid,
        double expected
    )
    {
        AudioEnergy.Estimate(lufs, centroid).Should().Be(expected);
    }

    /// <summary>
    /// One input present is a partial answer worth keeping, not a reason to
    /// discard a usable signal — but it must be that input alone, unweighted.
    /// </summary>
    [Fact]
    public void Estimate_UsesLoudnessAloneWhenBrightnessIsMissing()
    {
        AudioEnergy.Estimate(-9.0, null).Should().BeApproximately(0.84, 0.000001);
    }

    [Fact]
    public void Estimate_UsesBrightnessAloneWhenLoudnessIsMissing()
    {
        AudioEnergy.Estimate(null, 2400.0).Should().BeApproximately(0.422222, 0.000001);
    }

    [Fact]
    public void Estimate_ReturnsNullWhenNothingWasMeasured()
    {
        AudioEnergy.Estimate(null, null).Should().BeNull();
    }

    /// <summary>
    /// Louder must never score lower at equal brightness, and brighter must never
    /// score lower at equal loudness. A sign flip in either weight passes every
    /// single-point check above but breaks this.
    /// </summary>
    [Fact]
    public void Estimate_RisesWithBothInputs()
    {
        AudioEnergy
            .Estimate(-8.0, 2400.0)!
            .Value.Should()
            .BeGreaterThan(AudioEnergy.Estimate(-20.0, 2400.0)!.Value);

        AudioEnergy
            .Estimate(-9.0, 4000.0)!
            .Value.Should()
            .BeGreaterThan(AudioEnergy.Estimate(-9.0, 1000.0)!.Value);
    }
}
