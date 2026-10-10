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

using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Onboarding;

namespace NoMercy.Tests.OpticalMedia.Onboarding;

[Trait("Category", "Unit")]
public class DiscOnboardingSessionTests
{
    [Fact]
    public void WithState_ReturnsNewInstanceWithUpdatedStateAndTimestamp()
    {
        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");
        DiscOnboardingSession next = session.WithState(DiscOnboardingState.Probing);

        next.State.Should().Be(DiscOnboardingState.Probing);
        next.DrivePath.Should().Be("D:\\");
        next.UpdatedAt.Should().BeOnOrAfter(session.UpdatedAt);
        session
            .State.Should()
            .Be(DiscOnboardingState.Detected, "the original instance must be unchanged");
    }

    [Fact]
    public void Create_StartsInDetectedStateWithNoCandidates()
    {
        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");

        session.State.Should().Be(DiscOnboardingState.Detected);
        session.Candidates.Should().BeEmpty();
        session.SessionId.Should().NotBe(default(Guid));
    }

    [Fact]
    public void WithCandidates_SetsCandidatesAndState()
    {
        DiscCandidate candidate = new(
            Source: "tmdb",
            StableId: "27205",
            Title: "Inception",
            Year: 2010,
            PosterUrl: null,
            BackdropUrl: null,
            Confidence: 0.95
        );
        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");

        DiscOnboardingSession next = session.WithCandidates(
            [candidate],
            DiscOnboardingState.AwaitingConfirm
        );

        next.Candidates.Should().ContainSingle().Which.Title.Should().Be("Inception");
        next.State.Should().Be(DiscOnboardingState.AwaitingConfirm);
    }

    [Fact]
    public void WithJob_RecordsConfirmedTarget_ForLaterCompletionMatching()
    {
        Ulid libraryId = Ulid.NewUlid();
        DiscOnboardingSession session = DiscOnboardingSession.Create("D:\\");

        DiscOnboardingSession next = session.WithJob("job-1", libraryId, 27205, "movie");

        next.State.Should().Be(DiscOnboardingState.Ripping);
        next.JobId.Should().Be("job-1");
        next.LibraryId.Should().Be(libraryId);
        next.ConfirmedTmdbId.Should().Be(27205);
        next.ConfirmedMediaType.Should().Be("movie");
    }

    [Fact]
    public void WithCompletion_TransitionsToCompleteAndCarriesResultIdentity()
    {
        DiscOnboardingSession ripping = DiscOnboardingSession
            .Create("D:\\")
            .WithJob("job-1", Ulid.NewUlid(), 27205, "movie");

        DiscOnboardingSession completed = ripping.WithCompletion("movie", "27205");

        completed.State.Should().Be(DiscOnboardingState.Complete);
        completed.ResultType.Should().Be("movie");
        completed.ResultId.Should().Be("27205");
        completed.JobId.Should().Be("job-1", "completion must not lose the job that produced it");
    }
}
