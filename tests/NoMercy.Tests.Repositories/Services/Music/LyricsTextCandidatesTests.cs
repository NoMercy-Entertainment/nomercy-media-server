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
        await Task.Delay(TimeSpan.FromMilliseconds(150));
        await service.GetAsync(_trackId);

        first.Candidates.Should().ContainSingle(c => c.Source == LyricsSources.Embedded);
        duringBackoff.Candidates.Should().BeEmpty("inside the backoff nothing is fetched");
        _aggregator.Verify(a => a.SearchCandidatesAsync(It.IsAny<Track>()), Times.Exactly(2));
    }

    [Fact]
    public async Task An_unknown_track_has_no_text()
    {
        LyricsTextCandidates service = Service(TimeSpan.FromHours(24), TimeSpan.FromMinutes(2));

        (await service.GetAsync(Guid.NewGuid())).Candidates.Should().BeEmpty();
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
