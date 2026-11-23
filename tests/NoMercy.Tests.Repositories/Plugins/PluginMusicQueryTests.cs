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
using NoMercy.Database.Models.Libraries;
using NoMercy.Database.Models.Music;
using NoMercy.PluginSdk.Abstractions;

namespace NoMercy.Tests.Repositories.Plugins;

public class PluginMusicQueryTests : IDisposable
{
    private readonly SqliteConnection _connection;

    private readonly DbContextOptions<MediaContext> _options;

    private readonly Ulid _libraryId = Ulid.NewUlid();

    // A second library, with a track of its own and for a track that is in two.
    private readonly Ulid _otherLibraryId = Ulid.NewUlid();
    private readonly Guid _trackInTheOtherLibrary = Guid.NewGuid();

    private readonly Guid _trackWithDuration = Guid.NewGuid();

    private readonly Guid _trackWithoutDuration = Guid.NewGuid();

    private readonly Guid _hourLongTrack = Guid.NewGuid();

    public PluginMusicQueryTests()
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

        context.Libraries.AddRange(
            new Library
            {
                Id = _libraryId,
                Title = "Music",
                Type = "music",
            },
            new Library
            {
                Id = _otherLibraryId,
                Title = "Other music",
                Type = "music",
            }
        );

        context.Tracks.AddRange(
            new Track
            {
                Id = _trackWithDuration,
                Name = "With duration",
                TrackNumber = 3,
                DiscNumber = 1,
                Duration = "03:45",
            },
            new Track { Id = _trackWithoutDuration, Name = "Without duration" },
            new Track
            {
                Id = _hourLongTrack,
                Name = "Hour long",
                Duration = "01:02:03",
            },
            new Track { Id = _trackInTheOtherLibrary, Name = "In the other library" }
        );

        context.LibraryTrack.AddRange(
            new LibraryTrack { LibraryId = _libraryId, TrackId = _trackWithDuration },
            new LibraryTrack { LibraryId = _libraryId, TrackId = _trackWithoutDuration },
            new LibraryTrack { LibraryId = _libraryId, TrackId = _hourLongTrack },
            new LibraryTrack { LibraryId = _otherLibraryId, TrackId = _trackInTheOtherLibrary }
        );

        context.SaveChanges();
    }

    public void Dispose()
    {
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private PluginMusicQuery CreateQuery()
    {
        Mock<IDbContextFactory<MediaContext>> factory = new();
        factory
            .Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new(_options));

        return new PluginMusicQuery(factory.Object);
    }

    /// <summary>
    /// An id that is not a library id answers nothing, the way the DJ and stem
    /// queries beside it already do, rather than up to a page of every
    /// library's tracks.
    /// </summary>
    [Theory]
    [InlineData("not-a-library")]
    [InlineData("12345")]
    public async Task GetTracksAsync_WithAnIdThatIsNotALibraryId_ReturnsNothing(string libraryId)
    {
        (await CreateQuery().GetTracksAsync(libraryId)).Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksAsync_WithNoId_ReturnsTracksFromEveryLibrary()
    {
        IReadOnlyList<PluginTrack> every = await CreateQuery().GetTracksAsync(null);
        IReadOnlyList<PluginTrack> one = await CreateQuery().GetTracksAsync(_libraryId.ToString());

        every.Count.Should().BeGreaterThan(one.Count);
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsTheLibrarysTracks()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Should().HaveCount(3);
        tracks.Select(track => track.Id).Should().Contain(_trackWithDuration);
    }

    /// <summary>
    /// The library stores a duration as the "mm:ss" string ffprobe printed. A
    /// plugin planning a transition needs seconds, so the conversion is done
    /// here, once, rather than in every plugin.
    /// </summary>
    [Fact]
    public async Task GetTracksAsync_ConvertsTheStoredDurationToSeconds()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _trackWithDuration).DurationSeconds.Should().Be(225.0);
    }

    /// <summary>
    /// The leading "00:" is stripped when a track is stored, so anything of an
    /// hour or more keeps three parts and a shorter track has two.
    /// </summary>
    [Fact]
    public async Task GetTracksAsync_ReadsAnHourLongDurationAsHoursMinutesSeconds()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _hourLongTrack).DurationSeconds.Should().Be(3723.0);
    }

    [Fact]
    public async Task GetTracksAsync_LeavesDurationNullWhenTheLibraryHasNone()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(_libraryId.ToString());

        tracks.Single(track => track.Id == _trackWithoutDuration).DurationSeconds.Should().BeNull();
    }

    [Fact]
    public async Task GetTracksAsync_ReturnsNothingForAnotherLibrary()
    {
        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksAsync(Ulid.NewUlid().ToString());

        tracks.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksAsync_Pages()
    {
        PluginMusicQuery query = CreateQuery();

        IReadOnlyList<PluginTrack> first = await query.GetTracksAsync(
            _libraryId.ToString(),
            skip: 0,
            take: 2
        );
        IReadOnlyList<PluginTrack> second = await query.GetTracksAsync(
            _libraryId.ToString(),
            skip: 2,
            take: 2
        );

        first.Should().HaveCount(2);
        second.Should().HaveCount(1);
        first.Select(track => track.Id).Should().NotIntersectWith(second.Select(track => track.Id));
    }

    // --- GetTracksByIdAsync -----------------------------------------------

    /// <summary>
    /// A plugin that hears <c>music.lyrics.requested</c> holds a track id and
    /// nothing else. One call has to give it what a page of the library
    /// would: the length, the title, the artist.
    /// </summary>
    [Fact]
    public async Task GetTracksByIdAsync_ReturnsTheTrackExactlyAsAPageWouldShowIt()
    {
        Guid albumId = Guid.NewGuid();
        Guid artistId = Guid.NewGuid();
        using (MediaContext context = new(_options))
        {
            context.Albums.Add(
                new Album
                {
                    Id = albumId,
                    Name = "The Album",
                    LibraryId = _libraryId,
                    Library = null!,
                }
            );
            context.AlbumTrack.Add(
                new AlbumTrack { AlbumId = albumId, TrackId = _trackWithDuration }
            );
            context.Artists.Add(
                new Artist
                {
                    Id = artistId,
                    Name = "The Artist",
                    HostFolder = "/library/the-artist",
                }
            );
            context.ArtistTrack.Add(
                new ArtistTrack { ArtistId = artistId, TrackId = _trackWithDuration }
            );
            context.SaveChanges();
        }

        PluginMusicQuery query = CreateQuery();

        IReadOnlyList<PluginTrack> byId = await query.GetTracksByIdAsync([_trackWithDuration]);

        PluginTrack track = byId.Should().ContainSingle().Subject;
        track
            .Should()
            .Be(
                new PluginTrack(
                    _trackWithDuration,
                    "With duration",
                    "The Album",
                    "The Artist",
                    3,
                    1,
                    225.0,
                    _libraryId.ToString()
                )
                {
                    AlbumId = albumId,
                    Albums = [new(albumId, "The Album")],
                    Artists = [new(artistId, "The Artist")],
                }
            );

        IReadOnlyList<PluginTrack> page = await query.GetTracksAsync(_libraryId.ToString());
        track.Should().Be(page.Single(listed => listed.Id == _trackWithDuration));
    }

    [Fact]
    public async Task GetTracksByIdAsync_LeavesOutAnIdItDoesNotKnow()
    {
        PluginMusicQuery query = CreateQuery();

        (await query.GetTracksByIdAsync([Guid.NewGuid()])).Should().BeEmpty();

        IReadOnlyList<PluginTrack> mixed = await query.GetTracksByIdAsync([
            Guid.NewGuid(),
            _hourLongTrack,
            Guid.NewGuid(),
            _trackWithoutDuration,
        ]);
        mixed
            .Select(track => track.Id)
            .Should()
            .BeEquivalentTo([_hourLongTrack, _trackWithoutDuration]);
    }

    /// <summary>
    /// A track whose file has gone stays in the database but leaves every
    /// list. A plugin must not be handed one to run ffmpeg over.
    /// </summary>
    [Fact]
    public async Task GetTracksByIdAsync_LeavesOutATrackThatIsNotPlayable()
    {
        Guid missingId = Guid.NewGuid();
        using (MediaContext context = new(_options))
        {
            context.Tracks.Add(
                new Track
                {
                    Id = missingId,
                    Name = "Gone",
                    MissingSince = DateTime.UtcNow,
                }
            );
            context.LibraryTrack.Add(
                new LibraryTrack { LibraryId = _libraryId, TrackId = missingId }
            );
            context.SaveChanges();
        }

        IReadOnlyList<PluginTrack> tracks = await CreateQuery()
            .GetTracksByIdAsync([missingId, _trackWithDuration]);

        tracks.Select(track => track.Id).Should().Equal(_trackWithDuration);
    }

    [Fact]
    public async Task GetTracksByIdAsync_ReturnsNothingForAnEmptyRequest()
    {
        (await CreateQuery().GetTracksByIdAsync([])).Should().BeEmpty();
    }

    [Fact]
    public async Task GetTracksByIdAsync_ReturnsATrackOncePerLibrary_HoweverOftenItIsAsked()
    {
        PluginMusicQuery query = CreateQuery();

        (
            await query.GetTracksByIdAsync([
                _trackWithDuration,
                _trackWithDuration,
                _trackWithDuration,
            ])
        )
            .Should()
            .ContainSingle();

        // In a second library as well: once per library, as a page lists it.
        using (MediaContext context = new(_options))
        {
            context.LibraryTrack.Add(
                new LibraryTrack { LibraryId = _otherLibraryId, TrackId = _trackWithDuration }
            );
            context.SaveChanges();
        }

        IReadOnlyList<PluginTrack> inBoth = await query.GetTracksByIdAsync([
            _trackWithDuration,
            _trackWithDuration,
        ]);

        inBoth.Should().HaveCount(2);
        inBoth.Should().OnlyContain(track => track.Id == _trackWithDuration);
        inBoth
            .Select(track => track.LibraryId)
            .Should()
            .BeEquivalentTo([_libraryId.ToString(), _otherLibraryId.ToString()]);
    }

    [Fact]
    public async Task GetTracksByIdAsync_OrdersByTrackId_AsAPageDoes()
    {
        PluginMusicQuery query = CreateQuery();
        List<Guid> pageOrder = (await query.GetTracksAsync(_libraryId.ToString()))
            .Select(track => track.Id)
            .ToList();

        IReadOnlyList<PluginTrack> reversed = await query.GetTracksByIdAsync(
            pageOrder.AsEnumerable().Reverse().ToList()
        );

        reversed.Select(track => track.Id).Should().Equal(pageOrder);
    }

    /// <summary>
    /// The same cap a page has: a plugin handing over a hundred thousand ids
    /// reads the first thousand of them, not the whole library in one hop.
    /// </summary>
    [Fact]
    public async Task GetTracksByIdAsync_ReadsAtMostAPageOfIds()
    {
        const int page = 1000;
        List<Guid> strangers = Enumerable.Range(0, page).Select(_ => Guid.NewGuid()).ToList();
        PluginMusicQuery query = CreateQuery();

        IReadOnlyList<PluginTrack> pastTheCap = await query.GetTracksByIdAsync([
            .. strangers,
            _trackWithDuration,
        ]);
        IReadOnlyList<PluginTrack> insideTheCap = await query.GetTracksByIdAsync([
            .. strangers.Take(page - 1),
            _trackWithDuration,
        ]);

        pastTheCap.Should().BeEmpty();
        insideTheCap.Select(track => track.Id).Should().Equal(_trackWithDuration);
    }

    /// <summary>
    /// The member is default-implemented, so a host or a test double written
    /// against an older ABI keeps compiling. That fallback knows no track.
    /// </summary>
    [Fact]
    public async Task GetTracksByIdAsync_OnAnImplementerThatPredatesIt_KnowsNoTrack()
    {
        IPluginMusicQuery older = new Mock<IPluginMusicQuery> { CallBase = true }.Object;

        (await older.GetTracksByIdAsync([_trackWithDuration])).Should().BeEmpty();
    }
}
