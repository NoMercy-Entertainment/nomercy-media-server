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

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Database;
using NoMercy.Events;
using NoMercy.Events.Onboarding;
using NoMercy.NmSystem.Dto;
using NoMercy.OpticalMedia.Drives;
using NoMercy.OpticalMedia.Metadata;
using NoMercy.OpticalMedia.Onboarding;
using NoMercy.OpticalMedia.Sources;
using NoMercyQueue.Core.Interfaces;

namespace NoMercy.Tests.OpticalMedia.Onboarding;

[Trait("Category", "Unit")]
public class DiscOnboardingOrchestratorStartTests
{
    private static DiscDrive MakeDrive(OpticalDiscType type = OpticalDiscType.Dvd) =>
        new(Path: "D:\\", Label: "TEST_DISC", HasDisc: true, DiscType: type);

    private static DiscCandidate MakeCandidate(double confidence = 0.95) =>
        new(
            Source: "tmdb",
            StableId: "27205",
            Title: "Inception",
            Year: 2010,
            PosterUrl: null,
            BackdropUrl: null,
            Confidence: confidence
        );

    private static Mock<IDiscSource> MakeSource(OpticalDiscType type = OpticalDiscType.Dvd)
    {
        Mock<IDiscSource> source = new();
        source.Setup(s => s.Type).Returns(type);
        source
            .Setup(s => s.ProbeAsync(It.IsAny<DiscDrive>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
                new DiscInfo(OpticalDiscType.Dvd, "TEST_DISC", [], null, TimeSpan.FromMinutes(90))
            );
        return source;
    }

    [Fact]
    public async Task StartAsync_SingleCandidateAndLibraryAutoConfirmEnabled_TransitionsToAutoConfirmed()
    {
        Mock<IDiscSource> source = MakeSource();

        DiscCandidate candidate = MakeCandidate();
        DiscIdentificationService identificationService = new(
            [new StubIdentifier(candidate)],
            NullLogger<DiscIdentificationService>.Instance
        );
        DiscSourceFactory sourceFactory = new([source.Object]);
        DiscOnboardingSessionStore store = new();
        Mock<IEventBus> eventBus = new();

        DiscOnboardingOrchestrator orchestrator = new(
            sourceFactory,
            identificationService,
            store,
            eventBus.Object,
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IDriveMonitor>(),
            Mock.Of<IDbContextFactory<MediaContext>>()
        );

        DiscOnboardingSession result = await orchestrator.StartAsync(
            MakeDrive(),
            libraryAutoConfirmEnabled: true,
            CancellationToken.None
        );

        result.State.Should().Be(DiscOnboardingState.AutoConfirmed);
        result.Candidates.Should().ContainSingle();
        eventBus.Verify(
            b =>
                b.PublishAsync(
                    It.IsAny<DiscOnboardingStateChangedEvent>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.AtLeastOnce
        );
    }

    [Fact]
    public async Task StartAsync_SingleCandidateButLibraryAutoConfirmDisabled_StaysAwaitingConfirm()
    {
        Mock<IDiscSource> source = MakeSource();

        DiscIdentificationService identificationService = new(
            [new StubIdentifier(MakeCandidate())],
            NullLogger<DiscIdentificationService>.Instance
        );
        DiscOnboardingOrchestrator orchestrator = new(
            new DiscSourceFactory([source.Object]),
            identificationService,
            new DiscOnboardingSessionStore(),
            Mock.Of<IEventBus>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IDriveMonitor>(),
            Mock.Of<IDbContextFactory<MediaContext>>()
        );

        DiscOnboardingSession result = await orchestrator.StartAsync(
            MakeDrive(),
            libraryAutoConfirmEnabled: false,
            CancellationToken.None
        );

        result.State.Should().Be(DiscOnboardingState.AwaitingConfirm);
    }

    [Fact]
    public async Task StartAsync_TwoCompetingCandidatesEvenWithAutoConfirmEnabled_StaysAwaitingConfirm()
    {
        Mock<IDiscSource> source = MakeSource();

        DiscIdentificationService identificationService = new(
            [new StubIdentifier(MakeCandidate(0.6), MakeCandidate(0.55) with { StableId = "999" })],
            NullLogger<DiscIdentificationService>.Instance
        );
        DiscOnboardingOrchestrator orchestrator = new(
            new DiscSourceFactory([source.Object]),
            identificationService,
            new DiscOnboardingSessionStore(),
            Mock.Of<IEventBus>(),
            Mock.Of<IJobDispatcher>(),
            Mock.Of<IDriveMonitor>(),
            Mock.Of<IDbContextFactory<MediaContext>>()
        );

        DiscOnboardingSession result = await orchestrator.StartAsync(
            MakeDrive(),
            libraryAutoConfirmEnabled: true,
            CancellationToken.None
        );

        result.State.Should().Be(DiscOnboardingState.AwaitingConfirm);
        result.Candidates.Should().HaveCount(2);
    }

    private sealed class StubIdentifier : IDiscIdentifier
    {
        private readonly DiscCandidate[] _candidates;

        public StubIdentifier(params DiscCandidate[] candidates) => _candidates = candidates;

        public bool CanHandle(OpticalDiscType type) => true;

        public Task<DiscIdentification> IdentifyAsync(DiscInfo disc, CancellationToken ct) =>
            Task.FromResult(
                new DiscIdentification(
                    Kind: MediaKind.Movie,
                    Candidates: _candidates,
                    TopConfidence: _candidates.Length > 0 ? _candidates[0].Confidence : 0,
                    AutoApply: false,
                    NeedsManualAssignment: false
                )
            );
    }
}
