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
using Microsoft.Extensions.DependencyInjection;
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.Music;
using NoMercy.Database.Models.Music;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Lyrics;
using Xunit;

namespace NoMercy.Tests.Repositories.Services.Music;

/// <summary>
/// Covers the negative-cache classification added alongside the lyrics resolve
/// perf work: a confirmed "no lyrics anywhere" must persist the permanent "[]"
/// marker (so later plays never re-hit Lrclib/Musixmatch), but a transient
/// provider failure (timeout / rate limit / unexpected error) must NOT --
/// otherwise every hiccup would permanently blacklist a track that was simply
/// never checked. A short in-memory backoff still stops the very next play
/// from immediately re-running the same slow chain.
/// </summary>
[Trait("Category", "Unit")]
public class LyricsResolverTests
{
    private static Track MakeUncachedTrack() => new() { Id = Guid.NewGuid(), Name = "Track" };

    private static (
        LyricsResolver Resolver,
        Mock<IMusicRepository> Repository,
        Mock<ILyricsAggregator> Aggregator
    ) MakeResolver(Track track, TimeSpan? transientBackoff = null)
    {
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r => r.GetTrackWithIncludesAsync(track.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(track);

        Mock<ILyricsAggregator> aggregator = new();

        ServiceCollection services = new();
        services.AddSingleton(repository.Object);
        ServiceProvider provider = services.BuildServiceProvider();

        LyricsResolver resolver = transientBackoff is { } backoff
            ? new(provider.GetRequiredService<IServiceScopeFactory>(), aggregator.Object, backoff)
            : new(provider.GetRequiredService<IServiceScopeFactory>(), aggregator.Object);

        return (resolver, repository, aggregator);
    }

    [Fact]
    public async Task ResolveAsync_DefinitiveMiss_WritesPermanentEmptyMarker()
    {
        Track track = MakeUncachedTrack();
        (
            LyricsResolver resolver,
            Mock<IMusicRepository> repository,
            Mock<ILyricsAggregator> aggregator
        ) = MakeResolver(track);
        aggregator.Setup(a => a.SearchLyrics(track)).ReturnsAsync(LyricsFetchResult.NotFound);

        Lyric[]? result = await resolver.ResolveAsync(track.Id);

        result.Should().BeNull();
        repository.Verify(
            r => r.UpdateTrackLyricsAsync(track, "[]", It.IsAny<CancellationToken>()),
            Times.Once
        );
    }

    [Fact]
    public async Task ResolveAsync_TransientError_DoesNotWritePermanentMarker()
    {
        Track track = MakeUncachedTrack();
        (
            LyricsResolver resolver,
            Mock<IMusicRepository> repository,
            Mock<ILyricsAggregator> aggregator
        ) = MakeResolver(track);
        aggregator
            .Setup(a => a.SearchLyrics(track))
            .ReturnsAsync(LyricsFetchResult.TransientFailure);

        Lyric[]? result = await resolver.ResolveAsync(track.Id);

        result.Should().BeNull();
        repository.Verify(
            r =>
                r.UpdateTrackLyricsAsync(
                    It.IsAny<Track>(),
                    It.IsAny<string>(),
                    It.IsAny<CancellationToken>()
                ),
            Times.Never
        );
    }

    [Fact]
    public async Task ResolveAsync_TransientError_BacksOffInsteadOfRetryingImmediately()
    {
        Track track = MakeUncachedTrack();
        (LyricsResolver resolver, _, Mock<ILyricsAggregator> aggregator) = MakeResolver(
            track,
            transientBackoff: TimeSpan.FromMinutes(2)
        );
        aggregator
            .Setup(a => a.SearchLyrics(track))
            .ReturnsAsync(LyricsFetchResult.TransientFailure);

        await resolver.ResolveAsync(track.Id);
        await resolver.ResolveAsync(track.Id);

        // The second call landed inside the backoff window, so it must never
        // touch the aggregator (and therefore never re-hit the rate-limited
        // providers) a second time.
        aggregator.Verify(a => a.SearchLyrics(track), Times.Once);
    }

    [Fact]
    public async Task ResolveAsync_TransientError_RetriesForRealOnceBackoffExpires()
    {
        Track track = MakeUncachedTrack();
        (LyricsResolver resolver, _, Mock<ILyricsAggregator> aggregator) = MakeResolver(
            track,
            transientBackoff: TimeSpan.FromMilliseconds(20)
        );
        aggregator
            .Setup(a => a.SearchLyrics(track))
            .ReturnsAsync(LyricsFetchResult.TransientFailure);

        await resolver.ResolveAsync(track.Id);
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        await resolver.ResolveAsync(track.Id);

        aggregator.Verify(a => a.SearchLyrics(track), Times.Exactly(2));
    }

    [Fact]
    public async Task ResolveAsync_Found_PersistsSerializedLines()
    {
        Track track = MakeUncachedTrack();
        (
            LyricsResolver resolver,
            Mock<IMusicRepository> repository,
            Mock<ILyricsAggregator> aggregator
        ) = MakeResolver(track);
        LyricLine[] lines = [new() { Text = "line one" }];
        Lyric[] persisted = [new() { Text = "line one" }];
        aggregator
            .Setup(a => a.SearchLyrics(track))
            .ReturnsAsync(LyricsFetchResult.Found(lines, "Lrclib-get"));
        repository
            .Setup(r =>
                r.UpdateTrackLyricsAsync(
                    track,
                    It.Is<string>(json => json.Contains("line one")),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(persisted);

        Lyric[]? result = await resolver.ResolveAsync(track.Id);

        result.Should().BeEquivalentTo(persisted);
    }
}
