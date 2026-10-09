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

using NoMercy.Setup.Server;

namespace NoMercy.Tests.Setup.Server;

/// <summary>
/// The server owns one table of setup phase words. The setup page, the terminal
/// and the launcher tray all show these strings over the wire, so a phase never
/// has four different names again (issue #438).
/// </summary>
[Trait("Category", "Unit")]
public sealed class SetupPhaseWordsTests
{
    [Theory]
    [InlineData(SetupPhase.Unauthenticated, "Waiting for you to sign in")]
    [InlineData(SetupPhase.Authenticating, "Signing in")]
    [InlineData(SetupPhase.Authenticated, "Signed in")]
    [InlineData(SetupPhase.Registering, "Connecting to NoMercy")]
    [InlineData(SetupPhase.Registered, "Getting a secure address")]
    [InlineData(SetupPhase.CertificateAcquired, "Almost done")]
    [InlineData(SetupPhase.Failed, "Setup needs attention")]
    [InlineData(SetupPhase.Complete, "Ready")]
    public void Label_IsTheAgreedWording(SetupPhase phase, string expected)
    {
        Assert.Equal(expected, SetupPhaseWords.Label(phase));
    }

    [Fact]
    public void EveryPhase_HasALabelAndADetail()
    {
        foreach (SetupPhase phase in Enum.GetValues<SetupPhase>())
        {
            Assert.False(
                string.IsNullOrWhiteSpace(SetupPhaseWords.Label(phase)),
                $"{phase} has no label"
            );
            Assert.False(
                string.IsNullOrWhiteSpace(SetupPhaseWords.Detail(phase)),
                $"{phase} has no detail"
            );
        }
    }

    [Fact]
    public void RegisteringDetail_NamesTheTenMinuteCeiling()
    {
        string detail = SetupPhaseWords.Detail(SetupPhase.Registering);

        Assert.Contains("10 minutes", detail);
        Assert.DoesNotContain("couple of minutes", detail);
    }

    [Fact]
    public void SetupState_ExposesTheTableLabelAndDetail_ForItsCurrentPhase()
    {
        SetupState state = new();
        state.TransitionTo(SetupPhase.Authenticating);
        state.TransitionTo(SetupPhase.Authenticated);

        Assert.Equal(SetupPhaseWords.Label(SetupPhase.Authenticated), state.CurrentLabel);
        Assert.Equal(SetupPhaseWords.Detail(SetupPhase.Authenticated), state.PhaseDetail);
    }
}
