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
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Moq;
using NoMercy.Data.Services.Music;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.MediaProcessing.Lyrics;
using NoMercy.PluginSdk.Abstractions;
using NoMercy.Providers.Abstractions;
using NoMercy.Providers.Lyrics;
using NoMercy.Providers.NoMercy.Models;
using NoMercy.Storage;

namespace NoMercy.Tests.Repositories.Services.Music;

/// <summary>
/// Every text the host can find for a track, kept for a day, with a provider
/// hiccup held off rather than retried on the next call (#620).
/// </summary>
[Trait("Category", "Unit")]
public sealed class LyricsTextCandidatesTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Guid _trackId = Guid.NewGuid();
    private readonly Mock<ILyricsAggregator> _aggregator = new();
    private readonly Mock<IEmbeddedLyricsReader> _embedded = new();
    private readonly Mock<IStorageDriver> _driver = new();

    public LyricsTextCandidatesTests()
    {
        _connection = new("Data Source=:memory:");
        _connection.Open();
        using (SqliteCommand fkOff = _connection.CreateCommand())
        {
            fkOff.CommandText = "PRAGMA foreign_keys = OFF;";
            fkOff.ExecuteNonQuery();
        }
        _options = new DbContextOptionsBuilder<MediaContext>().UseSqlite(_connection).Options;

        using MediaContext context = new(_options);
        context.Database.EnsureCreated();
        context.Tracks.Add(
            new Track
            {
                Id = _trackId,
                Name = "Track",
                Duration = "03:45",
                FolderId = Ulid.NewUlid(),
                HostFolder = "/music/album",
                Filename = "/01 track.flac",
            }
        );
        context.SaveChanges();

        _driver
            .Setup(d => d.CombinePath(It.IsAny<string>(), It.IsAny<string[]>()))
            .Returns((string parent, string[] segments) => parent + string.Concat(segments));
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task Embedded_and_provider_texts_are_returned_as_text_only_and_kept_for_the_window()
    {
        _embedded
            .Setup(r => r.ReadAsync("/music/album/01 track.flac", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedLyrics(["embedded one"], false, "en"));
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.Is<Track>(t => t.Id == _trackId)))
            .ReturnsAsync(
                new LyricsCandidatesResult(
                    [
                        new(LyricsSources.Lrclib, Candidate("lrc one", "lrc two", synced: true)),
                        new(
                            LyricsSources.Musixmatch,
                            Candidate("mxm one", "mxm two", synced: false)
                        ),
                    ],
                    false
                )
            );
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        PluginLyricsText first = await service.GetAsync(_trackId);
        PluginLyricsText second = await service.GetAsync(_trackId);

        first.Candidates.Should().HaveCount(3);
        first.Candidates[0].Source.Should().Be(LyricsSources.Embedded);
        first.Candidates[0].Language.Should().Be("en");
        first.Candidates[1].Lines.Should().Equal("lrc one", "lrc two");
        first.Candidates[1].WasSynced.Should().BeTrue();
        first.Candidates[2].Source.Should().Be(LyricsSources.Musixmatch);
        first.IsComplete.Should().BeTrue();
        first.AskAgainAfter.Should().BeNull();
        second.Should().BeSameAs(first, "the answer is kept for a day");
        _aggregator.Verify(a => a.SearchCandidatesAsync(It.IsAny<Track>()), Times.Once);
    }

    [Fact]
    public async Task A_provider_hiccup_is_not_kept_and_backs_off_before_the_next_attempt()
    {
        _embedded
            .Setup(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedLyrics(["embedded one"], false, null));
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(LyricsCandidatesResult.TransientFailure);
        LyricsTextCandidates service = Service(
            TimeSpan.FromHours(24),
            TimeSpan.FromMilliseconds(50)
        );

        PluginLyricsText first = await service.GetAsync(_trackId);
        PluginLyricsText duringBackoff = await service.GetAsync(_trackId);
        _aggregator.Verify(
            a => a.SearchCandidatesAsync(It.IsAny<Track>()),
            Times.Once,
            "inside the backoff nothing is fetched"
        );
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await service.GetAsync(_trackId);

        first.Candidates.Should().ContainSingle(c => c.Source == LyricsSources.Embedded);
        duringBackoff.Should().BeSameAs(first, "the partial answer is kept next to the backoff");
        _aggregator.Verify(a => a.SearchCandidatesAsync(It.IsAny<Track>()), Times.Exactly(2));
    }

    /// <summary>
    /// "No text exists" is final and kept for a day. A plugin that reads an
    /// empty answer has to be able to tell it from the three below.
    /// </summary>
    [Fact]
    public async Task No_text_and_no_provider_error_is_an_empty_complete_answer_that_is_kept()
    {
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(new LyricsCandidatesResult([], false));
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        PluginLyricsText first = await service.GetAsync(_trackId);
        PluginLyricsText second = await service.GetAsync(_trackId);

        first.Candidates.Should().BeEmpty();
        first.IsComplete.Should().BeTrue();
        first.AskAgainAfter.Should().BeNull();
        second.Should().BeSameAs(first);
        _aggregator.Verify(a => a.SearchCandidatesAsync(It.IsAny<Track>()), Times.Once);
    }

    [Fact]
    public async Task A_provider_error_without_embedded_lyrics_is_empty_incomplete_and_says_when_to_ask_again()
    {
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(LyricsCandidatesResult.TransientFailure);
        TimeSpan backoff = TimeSpan.FromMinutes(2);
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), backoff);

        DateTimeOffset before = DateTimeOffset.UtcNow;
        PluginLyricsText text = await service.GetAsync(_trackId);
        DateTimeOffset after = DateTimeOffset.UtcNow;

        text.Candidates.Should().BeEmpty();
        text.IsComplete.Should().BeFalse("a provider that failed may have had the text");
        text.AskAgainAfter.Should().NotBeNull();
        text.AskAgainAfter!.Value.Should().BeOnOrAfter(before + backoff);
        text.AskAgainAfter!.Value.Should().BeOnOrBefore(after + backoff);
        text.AskAgainAfter!.Value.Offset.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public async Task A_provider_error_with_embedded_lyrics_returns_the_embedded_text_incomplete()
    {
        _embedded
            .Setup(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedLyrics(["embedded one"], false, "en"));
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(LyricsCandidatesResult.TransientFailure);
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        PluginLyricsText text = await service.GetAsync(_trackId);

        text.Candidates.Should().ContainSingle(c => c.Source == LyricsSources.Embedded);
        text.IsComplete.Should().BeFalse();
        text.AskAgainAfter.Should().NotBeNull();
    }

    /// <summary>
    /// A track with embedded lyrics never gets an empty answer only because a
    /// provider is backed off: for those two minutes the answer used to be
    /// the empty list, embedded text left out with the rest.
    /// </summary>
    [Fact]
    public async Task Inside_the_backoff_the_answer_is_incomplete_and_still_carries_the_embedded_text()
    {
        _embedded
            .Setup(r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EmbeddedLyrics(["embedded one"], false, "en"));
        _aggregator
            .Setup(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(LyricsCandidatesResult.TransientFailure);
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        PluginLyricsText failedFetch = await service.GetAsync(_trackId);
        PluginLyricsText duringBackoff = await service.GetAsync(_trackId);

        duringBackoff.IsComplete.Should().BeFalse();
        duringBackoff.AskAgainAfter.Should().Be(failedFetch.AskAgainAfter);
        duringBackoff.Candidates.Should().ContainSingle(c => c.Source == LyricsSources.Embedded);
        duringBackoff.Candidates[0].Lines.Should().Equal("embedded one");
        _embedded.Verify(
            r => r.ReadAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once,
            "the file is not read again inside the backoff either"
        );
    }

    [Fact]
    public async Task After_the_backoff_a_provider_that_answers_makes_the_answer_complete_again()
    {
        _aggregator
            .SetupSequence(a => a.SearchCandidatesAsync(It.IsAny<Track>()))
            .ReturnsAsync(LyricsCandidatesResult.TransientFailure)
            .ReturnsAsync(
                new LyricsCandidatesResult(
                    [new(LyricsSources.Lrclib, Candidate("lrc one", "lrc two", synced: true))],
                    false
                )
            );
        LyricsTextCandidates service = Service(
            TimeSpan.FromHours(24),
            TimeSpan.FromMilliseconds(50)
        );

        PluginLyricsText failed = await service.GetAsync(_trackId);
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        PluginLyricsText answered = await service.GetAsync(_trackId);

        failed.IsComplete.Should().BeFalse();
        answered.IsComplete.Should().BeTrue();
        answered.AskAgainAfter.Should().BeNull();
        answered.Candidates.Should().ContainSingle(c => c.Source == LyricsSources.Lrclib);
    }

    [Fact]
    public async Task An_unknown_track_has_no_text()
    {
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        PluginLyricsText text = await service.GetAsync(Guid.NewGuid());

        text.Candidates.Should().BeEmpty();
        text.IsComplete.Should().BeTrue("no later ask will find a track that is not there");
        _aggregator.Verify(a => a.SearchCandidatesAsync(It.IsAny<Track>()), Times.Never);
    }

    private LyricsTextCandidates Service(TimeSpan cacheWindow, TimeSpan backoff)
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return new(
            factory.Object,
            _aggregator.Object,
            _embedded.Object,
            _driver.Object,
            cacheWindow,
            backoff
        );
    }

    private static LyricCandidate Candidate(string first, string second, bool synced) =>
        new(
            "Track",
            "Artist",
            225,
            synced,
            [new LyricLine { Text = first }, new LyricLine { Text = second }]
        );
}
