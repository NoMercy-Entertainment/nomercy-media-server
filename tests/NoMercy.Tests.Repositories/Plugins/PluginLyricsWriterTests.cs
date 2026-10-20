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
using NoMercy.Data.Plugins;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

/// <summary>
/// The host side of <see cref="IPluginLyrics" />: every refusal the writer can
/// hand back, and that a valid write lands with generated timing, the text's
/// own source, no offset and a bumped version (#620).
/// </summary>
[Trait("Category", "Unit")]
public sealed class PluginLyricsWriterTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DbContextOptions<MediaContext> _options;
    private readonly Ulid _pluginId = Ulid.NewUlid();

    // "03:45": every line below 225 s is inside the track.
    private readonly Guid _neverChecked = Guid.NewGuid();
    private readonly Guid _providerSynced = Guid.NewGuid();
    private readonly Guid _legacySynced = Guid.NewGuid();
    private readonly Guid _plainText = Guid.NewGuid();
    private readonly Guid _generated = Guid.NewGuid();

    public PluginLyricsWriterTests()
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
        context.Tracks.AddRange(
            Track(_neverChecked, lyrics: null, source: null, timing: null),
            Track(
                _providerSynced,
                lyrics: [Line("one", 1.0), Line("two", 5.0)],
                source: LyricsSources.Lrclib,
                timing: LyricsTimings.Provider,
                offset: 250
            ),
            Track(_legacySynced, lyrics: [Line("one", 1.0)], source: null, timing: null),
            Track(
                _plainText,
                lyrics: [Line("one", 0), Line("two", 0)],
                source: LyricsSources.Musixmatch,
                timing: LyricsTimings.None,
                offset: 500
            ),
            Track(
                _generated,
                lyrics: [Line("one", 1.0)],
                source: LyricsSources.Lrclib,
                timing: LyricsTimings.Generated,
                version: 3
            )
        );
        context.SaveChanges();
    }

    public void Dispose() => _connection.Dispose();

    [Fact]
    public async Task GetState_ReportsLinesProvenanceOffsetAndVersion()
    {
        PluginLyricsState? state = await Writer().GetStateAsync(_providerSynced);

        state.Should().NotBeNull();
        state!.Lines.Should().HaveCount(2);
        state.Lines![1].Start.Should().Be(5.0);
        state.Source.Should().Be(LyricsSources.Lrclib);
        state.Timing.Should().Be(LyricsTimings.Provider);
        state.HasSyncedTiming.Should().BeTrue();
        state.OffsetMs.Should().Be(250);
        state.Version.Should().Be(0);
    }

    [Fact]
    public async Task GetState_TellsNeverCheckedFromCheckedWithNothingFound()
    {
        (await Writer().GetStateAsync(_neverChecked))!.Lines.Should().BeNull();

        await using (MediaContext context = new(_options))
        {
            Track track = await context.Tracks.SingleAsync(t => t.Id == _neverChecked);
            track.Lyrics = [];
            await context.SaveChangesAsync();
        }

        (await Writer().GetStateAsync(_neverChecked))!.Lines.Should().BeEmpty();
    }

    [Fact]
    public async Task GetState_LegacyRowWithTimes_HasSyncedTimingWithoutProvenance()
    {
        PluginLyricsState? state = await Writer().GetStateAsync(_legacySynced);

        state!.Timing.Should().BeNull();
        state.HasSyncedTiming.Should().BeTrue();
    }

    [Fact]
    public async Task GetState_UnknownTrack_IsNull()
    {
        (await Writer().GetStateAsync(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task Write_RefusesAnUnknownTrack()
    {
        PluginWriteResult result = await Writer().WriteAsync(Guid.NewGuid(), ValidWrite(0));

        result.Refusal.Should().StartWith(PluginLyricsRefusal.UnknownTrack);
    }

    [Fact]
    public async Task Write_RefusesAStaleVersion()
    {
        PluginWriteResult result = await Writer()
            .WriteAsync(_generated, ValidWrite(expectedVersion: 2));

        result.Refusal.Should().StartWith(PluginLyricsRefusal.VersionConflict);
        result.Refusal.Should().Contain("version 3");
    }

    [Fact]
    public async Task Write_NeverOverwritesSyncedProviderLyrics()
    {
        PluginWriteResult result = await Writer().WriteAsync(_providerSynced, ValidWrite(0));

        result.Refusal.Should().StartWith(PluginLyricsRefusal.ProviderSynced);
    }

    [Fact]
    public async Task Write_TreatsALegacyRowWithTimesAsSyncedProviderLyrics()
    {
        PluginWriteResult result = await Writer().WriteAsync(_legacySynced, ValidWrite(0));

        result.Refusal.Should().StartWith(PluginLyricsRefusal.ProviderSynced);
    }

    [Fact]
    public async Task Write_RefusesAnUnknownSource()
    {
        PluginLyricsWrite write = new([new("one", 1.0, null)], "genius", 0);

        PluginWriteResult result = await Writer().WriteAsync(_plainText, write);

        result.Refusal.Should().StartWith(PluginLyricsRefusal.InvalidSource);
    }

    public static TheoryData<string, PluginLyricLine[]> InvalidLines =>
        new()
        {
            { "no lines", [] },
            { "out of order", [new("two", 5.0, null), new("one", 1.0, null)] },
            { "before the track", [new("one", -0.5, null)] },
            { "after the track ends", [new("one", 300.0, null)] },
            {
                "first word not at the line start",
                [new("one two", 1.0, [new("one", 1.2, 1.4), new("two", 1.5, 1.8)])]
            },
            {
                "word ends before it starts",
                [new("one two", 1.0, [new("one", 1.0, 0.8), new("two", 1.5, 1.8)])]
            },
            {
                "word times not monotonic",
                [new("one two", 1.0, [new("one", 1.0, 1.4), new("two", 0.9, 1.8)])]
            },
        };

    [Theory]
    [MemberData(nameof(InvalidLines))]
    public async Task Write_RefusesLinesThatAreNotATimeline(string _, PluginLyricLine[] lines)
    {
        PluginLyricsWrite write = new(lines, LyricsSources.Lrclib, 0);

        PluginWriteResult result = await Writer().WriteAsync(_plainText, write);

        result.Refusal.Should().StartWith(PluginLyricsRefusal.InvalidLines);
    }

    [Fact]
    public async Task Write_WithoutAKnownDuration_SkipsTheUpperBound()
    {
        Guid noDuration = Guid.NewGuid();
        await using (MediaContext context = new(_options))
        {
            Track track = Track(noDuration, lyrics: null, source: null, timing: null);
            track.Duration = string.Empty;
            context.Tracks.Add(track);
            await context.SaveChangesAsync();
        }
        PluginLyricsWrite write = new([new("late", 9000.0, null)], LyricsSources.Lrclib, 0);

        PluginWriteResult result = await Writer().WriteAsync(noDuration, write);

        result.Ok.Should().BeTrue();
    }

    [Fact]
    public async Task AValidWrite_LandsAsGeneratedTiming_KeepsTheSource_ClearsTheOffset_AndBumpsTheVersion()
    {
        PluginLyricsWrite write = new(
            [
                new("one two", 1.0, [new("one", 1.0, 1.4), new("two", 1.5, 1.9)]),
                new("three", 5.25, null),
            ],
            LyricsSources.Musixmatch,
            ExpectedVersion: 0
        );

        PluginWriteResult result = await Writer().WriteAsync(_plainText, write);

        result.Ok.Should().BeTrue(result.Refusal);
        Track stored = await ReadTrack(_plainText);
        stored.LyricsTiming.Should().Be(LyricsTimings.Generated);
        stored.LyricsSource.Should().Be(LyricsSources.Musixmatch);
        stored.LyricsOffset.Should().BeNull("the offset was tuned for the previous timing");
        stored.LyricsVersion.Should().Be(1);
        stored.Lyrics.Should().HaveCount(2);
        stored.Lyrics![0].Time.Total.Should().Be(1.0);
        stored.Lyrics[0].Time.Seconds.Should().Be(1);
        stored.Lyrics[0].Words.Should().HaveCount(2);
        stored.Lyrics[0].Words![1].End.Should().Be(1.9);
        stored.Lyrics[1].Time.Total.Should().Be(5.25);
        stored.Lyrics[1].Time.Hundredths.Should().Be(25);
        stored.Lyrics[1].Words.Should().BeNull();

        PluginLyricsState? state = await Writer().GetStateAsync(_plainText);
        state!.Version.Should().Be(1);
        state.Timing.Should().Be(LyricsTimings.Generated);
        state.Lines![0].Words.Should().HaveCount(2);
    }

    [Fact]
    public async Task AValidWrite_OverGeneratedLyrics_ReplacesThemAtTheVersionRead()
    {
        PluginWriteResult result = await Writer()
            .WriteAsync(_generated, ValidWrite(expectedVersion: 3));

        result.Ok.Should().BeTrue(result.Refusal);
        (await ReadTrack(_generated)).LyricsVersion.Should().Be(4);
    }

    [Fact]
    public async Task Clear_RefusesAnythingButGeneratedLyrics()
    {
        PluginWriteResult result = await Writer().ClearAsync(_providerSynced, expectedVersion: 0);

        result.Refusal.Should().StartWith(PluginLyricsRefusal.NotGenerated);
        (await ReadTrack(_providerSynced)).Lyrics.Should().HaveCount(2);
    }

    [Fact]
    public async Task Clear_RefusesAStaleVersion()
    {
        PluginWriteResult result = await Writer().ClearAsync(_generated, expectedVersion: 1);

        result.Refusal.Should().StartWith(PluginLyricsRefusal.VersionConflict);
    }

    [Fact]
    public async Task Clear_RestoresNeverChecked_AndBumpsTheVersion()
    {
        PluginWriteResult result = await Writer().ClearAsync(_generated, expectedVersion: 3);

        result.Ok.Should().BeTrue(result.Refusal);
        Track stored = await ReadTrack(_generated);
        stored.Lyrics.Should().BeNull("the resolver must fetch provider lyrics again");
        stored.LyricsSource.Should().BeNull();
        stored.LyricsTiming.Should().BeNull();
        stored.LyricsVersion.Should().Be(4);
    }

    private PluginLyricsWriter Writer() => new(_pluginId, ContextFactory());

    private IDbContextFactory<MediaContext> ContextFactory()
    {
        Mock<IDbContextFactory<MediaContext>> mock = new();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));
        return mock.Object;
    }

    private async Task<Track> ReadTrack(Guid id)
    {
        await using MediaContext context = new(_options);
        return await context.Tracks.AsNoTracking().SingleAsync(t => t.Id == id);
    }

    private static PluginLyricsWrite ValidWrite(long expectedVersion) =>
        new([new("one", 1.0, null), new("two", 5.0, null)], LyricsSources.Lrclib, expectedVersion);

    private static Lyric Line(string text, double total) =>
        new()
        {
            Text = text,
            Time = new() { Total = total },
        };

    private static Track Track(
        Guid id,
        Lyric[]? lyrics,
        string? source,
        string? timing,
        int? offset = null,
        long version = 0
    ) =>
        new()
        {
            Id = id,
            Name = "Track",
            Duration = "03:45",
            FolderId = Ulid.NewUlid(),
            Lyrics = lyrics,
            LyricsSource = source,
            LyricsTiming = timing,
            LyricsOffset = offset,
            LyricsVersion = version,
        };
}
