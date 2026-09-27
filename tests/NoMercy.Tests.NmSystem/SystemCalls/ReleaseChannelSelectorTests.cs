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

using NoMercy.NmSystem.Configuration;
using NoMercy.NmSystem.SystemCalls;

namespace NoMercy.Tests.NmSystem.SystemCalls;

/// <summary>
/// The release channels as promote-release.yml publishes them: a nightly is a
/// GitHub prerelease, a beta is a prerelease carrying the beta marker, a
/// stable is a full release. A retracted release carries the RETRACTED notice
/// retract-release.yml prepends and is never offered.
/// </summary>
[Trait("Category", "Unit")]
public class ReleaseChannelSelectorTests
{
    private const string BetaMarker = "<!-- nomercy-channel: beta -->";
    private const string StableMarker = "<!-- nomercy-channel: stable -->";

    private static ReleaseSummary Nightly(string tag) => new(tag, false, true, "## Install");

    private static ReleaseSummary Beta(string tag) =>
        new(tag, false, true, $"{BetaMarker}\n## Install");

    private static ReleaseSummary Stable(string tag) =>
        new(tag, false, false, $"{StableMarker}\n## Install");

    private static ReleaseSummary LegacyStable(string tag) => new(tag, false, false, "## Install");

    private static ReleaseSummary? Pick(ReleaseChannel channel, params ReleaseSummary[] releases) =>
        ReleaseChannelSelector.Select(releases, r => r, channel);

    [Fact]
    public void Stable_TakesTheNewestFullRelease_AndIgnoresNewerBetasAndNightlies()
    {
        ReleaseSummary? picked = Pick(
            ReleaseChannel.Stable,
            Nightly("v1.0.20"),
            Beta("v1.0.14"),
            Stable("v1.0.0"),
            LegacyStable("v0.1.532")
        );

        picked!.TagName.Should().Be("v1.0.0");
    }

    [Fact]
    public void Beta_TakesTheNewestBetaOrStable_AndIgnoresNightlies()
    {
        Pick(ReleaseChannel.Beta, Nightly("v1.0.20"), Beta("v1.0.14"), Stable("v1.0.0"))!
            .TagName.Should()
            .Be("v1.0.14");
    }

    [Fact]
    public void Beta_FallsBackToStable_WhenStableIsNewerThanEveryBeta()
    {
        Pick(ReleaseChannel.Beta, Beta("v1.0.14"), Stable("v1.0.21"), Nightly("v1.0.22"))!
            .TagName.Should()
            .Be("v1.0.21");
    }

    [Fact]
    public void Nightly_TakesTheNewestOfAnyChannel()
    {
        Pick(ReleaseChannel.Nightly, Beta("v1.0.14"), Nightly("v1.0.20"), Stable("v1.0.0"))!
            .TagName.Should()
            .Be("v1.0.20");
    }

    [Fact]
    public void ComparesVersionsNumerically_NotAsText()
    {
        Pick(ReleaseChannel.Nightly, Nightly("v1.0.9"), Nightly("v1.0.10"))!
            .TagName.Should()
            .Be("v1.0.10");
    }

    [Fact]
    public void NeverOffersDraftsRetractedReleasesOrUnparseableTags()
    {
        ReleaseSummary? picked = Pick(
            ReleaseChannel.Nightly,
            new("v1.0.30", true, true, "## Install"),
            new("v1.0.29", false, true, "> [!CAUTION]\n> **RETRACTED v1.0.29.** broke playback"),
            new("v1.0.28-feature-branch", false, true, "## Install"),
            Nightly("v1.0.27")
        );

        picked!.TagName.Should().Be("v1.0.27");
    }

    [Fact]
    public void ReturnsNull_WhenNothingQualifies()
    {
        Pick(ReleaseChannel.Stable, Nightly("v1.0.20"), Beta("v1.0.14")).Should().BeNull();
    }

    [Theory]
    [InlineData("stable", ReleaseChannel.Stable)]
    [InlineData("Beta", ReleaseChannel.Beta)]
    [InlineData("NIGHTLY", ReleaseChannel.Nightly)]
    public void TryParse_AcceptsEveryChannelInAnyCase(string value, ReleaseChannel expected)
    {
        ReleaseChannelSelector.TryParse(value, out ReleaseChannel channel).Should().BeTrue();
        channel.Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("alpha")]
    [InlineData("1")]
    [InlineData("99")]
    public void TryParse_RejectsAnythingElse(string value)
    {
        ReleaseChannelSelector.TryParse(value, out _).Should().BeFalse();
    }
}
