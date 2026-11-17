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

using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NoMercy.Encoder.ContentAnalysis.Segments;
using NoMercy.Encoder.ContentAnalysis.Segments.Probes;
using NoMercy.MediaProcessing.Segments;
using NoMercy.Storage;

namespace NoMercy.Tests.MediaProcessing.Segments;

/// <summary>How one season run ends, and what it stores.</summary>
[Trait("Category", "Unit")]
public class SegmentAnalysisServiceTests
{
    private readonly Mock<ISegmentSourceResolver> _resolver = new();
    private readonly Mock<ISeasonSegmentPipeline> _pipeline = new();
    private readonly Mock<ISegmentAnalysisWriter> _writer = new();
    private IReadOnlyList<AnalysedItem>? _stored;

    public SegmentAnalysisServiceTests()
    {
        IStorage storage = Mock.Of<IStorage>();
        _resolver
            .Setup(r => r.ResolveSeasonAsync(10, It.IsAny<CancellationToken>()))
            .ReturnsAsync([
                .. Enumerable
                    .Range(1, 3)
                    .Select(id => new ResolvedSegmentSource(
                        id,
                        null,
                        new(storage, $"e{id}", 0, $"e{id}")
                    )),
            ]);
        _writer
            .Setup(w =>
                w.WriteAsync(It.IsAny<IReadOnlyList<AnalysedItem>>(), It.IsAny<CancellationToken>())
            )
            .Callback<IReadOnlyList<AnalysedItem>, CancellationToken>((items, _) => _stored = items)
            .Returns(Task.CompletedTask);
    }

    [Fact]
    public async Task ASeasonWithAnUnreadableEpisode_StoresTheOthers_ButIsNotDone()
    {
        // Episode 2's file could not be measured (a mount dropped mid-run).
        Answers("1", "3");

        SegmentAnalysisOutcome outcome = await Service()
            .AnalyseSeasonAsync(10, CancellationToken.None);

        outcome.Should().Be(SegmentAnalysisOutcome.PartlyMeasured);
        _stored!.Select(i => i.EpisodeId).Should().Equal(1, 3);
    }

    [Fact]
    public async Task ASeasonWhoseEveryEpisodeWasMeasured_IsAnalysed()
    {
        Answers("1", "2", "3");

        (await Service().AnalyseSeasonAsync(10, CancellationToken.None))
            .Should()
            .Be(SegmentAnalysisOutcome.Analysed);
        _stored.Should().HaveCount(3);
    }

    [Fact]
    public async Task ASeasonWithNothingMeasured_StoresNothing()
    {
        Answers();

        (await Service().AnalyseSeasonAsync(10, CancellationToken.None))
            .Should()
            .Be(SegmentAnalysisOutcome.NothingMeasured);
        _stored.Should().BeNull();
    }

    private void Answers(params string[] keys) =>
        _pipeline
            .Setup(p =>
                p.AnalyseAsync(
                    It.IsAny<IReadOnlyList<SegmentAnalysisInput>>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync([.. keys.Select(k => SegmentAnswers.Episode(k))]);

    private SegmentAnalysisService Service() =>
        new(
            _resolver.Object,
            _pipeline.Object,
            _writer.Object,
            NullLogger<SegmentAnalysisService>.Instance
        );
}
