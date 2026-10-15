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

using Moq;
using NoMercy.Api.Services;
using NoMercy.Encoder.Analysis;
using Xunit;

namespace NoMercy.Tests.Api.Services;

public sealed class StoredMediaAnalysisTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "nm-stored-analysis-" + Guid.NewGuid().ToString("N")
    );

    private static readonly Ulid FileId = Ulid.Parse("01JA0000000000000000000001");
    private static readonly DateTime Changed = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static MediaInfo Info(string format) =>
        new("movie.mp4", format, TimeSpan.FromMinutes(90), 8000, 5_000_000_000, [], [], [], []);

    [Fact]
    public async Task A_second_start_of_the_same_file_reuses_the_stored_analysis()
    {
        Mock<IMediaAnalyzer> analyzer = new();
        analyzer
            .Setup(a => a.AnalyzeAsync("movie.mp4", (string[]?)null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Info("mov,mp4"));
        StoredMediaAnalysis stored = new(analyzer.Object, _folder);

        MediaInfo first = await stored.GetOrAnalyzeAsync(
            FileId,
            Changed,
            "movie.mp4",
            null,
            default
        );
        MediaInfo second = await stored.GetOrAnalyzeAsync(
            FileId,
            Changed,
            "movie.mp4",
            null,
            default
        );

        second.Format.Should().Be(first.Format);
        analyzer.Verify(
            a => a.AnalyzeAsync("movie.mp4", (string[]?)null, It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task A_file_changed_since_its_analysis_is_analyzed_again()
    {
        Mock<IMediaAnalyzer> analyzer = new();
        analyzer
            .SetupSequence(a =>
                a.AnalyzeAsync("movie.mp4", (string[]?)null, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(Info("old"))
            .ReturnsAsync(Info("new"));
        StoredMediaAnalysis stored = new(analyzer.Object, _folder);

        await stored.GetOrAnalyzeAsync(FileId, Changed, "movie.mp4", null, default);
        MediaInfo after = await stored.GetOrAnalyzeAsync(
            FileId,
            Changed.AddMinutes(1),
            "movie.mp4",
            null,
            default
        );

        after.Format.Should().Be("new");
    }

    [Fact]
    public async Task A_failed_analysis_is_not_stored()
    {
        Mock<IMediaAnalyzer> analyzer = new();
        analyzer
            .SetupSequence(a =>
                a.AnalyzeAsync("movie.mp4", (string[]?)null, It.IsAny<CancellationToken>())
            )
            .ThrowsAsync(new IOException("storage unreachable"))
            .ReturnsAsync(Info("mov,mp4"));
        StoredMediaAnalysis stored = new(analyzer.Object, _folder);

        Func<Task> failing = () =>
            stored.GetOrAnalyzeAsync(FileId, Changed, "movie.mp4", null, default);
        await failing.Should().ThrowAsync<IOException>();
        MediaInfo retried = await stored.GetOrAnalyzeAsync(
            FileId,
            Changed,
            "movie.mp4",
            null,
            default
        );

        retried.Format.Should().Be("mov,mp4");
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }
}
