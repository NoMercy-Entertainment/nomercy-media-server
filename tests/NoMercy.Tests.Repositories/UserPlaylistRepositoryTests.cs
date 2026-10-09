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

using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NoMercy.Data.Repositories;
using NoMercy.Database;
using NoMercy.Database.Models.Music;
using NoMercy.Database.Models.Playlists;
using NoMercy.Database.Models.TvShows;
using NoMercy.Tests.Repositories.Infrastructure;

namespace NoMercy.Tests.Repositories;

/// <summary>
/// Coverage for the user-created, VIDEO-ONLY playlist slice: the UserPlaylist
/// container entity + PlaylistItem schema + UserPlaylistRepository. Backed by
/// its own UserPlaylists table — entirely separate from, and never touching,
/// the music-only Playlist/PlaylistTrack tables/read path owned by
/// MusicRepository.Playlists.cs. <see cref="ExistingMusicPlaylist_PlaylistTrackPath_StillWorks"/>
/// proves both directions of that separation: the legacy music playlist round
/// trips correctly through MusicRepository, and it never surfaces through
/// UserPlaylistRepository.
/// </summary>
[Trait("Category", "Unit")]
public class UserPlaylistRepositoryTests : IDisposable
{
    private readonly MediaContext _context;
    private readonly IDbContextFactory<MediaContext> _factory;
    private readonly SqliteConnection _connection;
    private readonly UserPlaylistRepository _repository;

    private static readonly Guid OtherUserId = Guid.Parse("e0000001-0000-0000-0000-000000000002");

    public UserPlaylistRepositoryTests()
    {
        (_factory, _connection) = TestMediaContextFactory.CreateSeededFactory();
        _context = _factory.CreateDbContext();
        _repository = new(_factory);

        _context.Users.Add(
            new()
            {
                Id = OtherUserId,
                Email = "other@nomercy.tv",
                Name = "Other User",
                Owner = false,
                Allowed = true,
                Manage = false,
            }
        );
        _context.SaveChanges();
    }

    private Track SeedTrack(string name = "Test Track")
    {
        Track track = new()
        {
            Id = Guid.NewGuid(),
            Name = name,
            TrackNumber = 1,
            DiscNumber = 1,
            FolderId = SeedConstants.MovieFolderId,
        };
        _context.Tracks.Add(track);
        _context.SaveChanges();
        return track;
    }

    private Special SeedSpecial(string title = "Test Special")
    {
        Special special = new() { Id = Ulid.NewUlid(), Title = title };
        _context.Specials.Add(special);
        _context.SaveChanges();
        return special;
    }

    [Fact]
    public async Task CreatePlaylistAsync_PersistsPlaylist_OwnedByCaller()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(
            SeedConstants.UserId,
            "My Video Playlist",
            "a description",
            "/cover.jpg"
        );

        UserPlaylist? saved = await _context.UserPlaylists.FirstOrDefaultAsync(p =>
            p.Id == playlistId
        );
        Assert.NotNull(saved);
        Assert.Equal("My Video Playlist", saved!.Name);
        Assert.Equal("a description", saved.Description);
        Assert.Equal("/cover.jpg", saved.Cover);
        Assert.Equal(SeedConstants.UserId, saved.UserId);
    }

    [Fact]
    public async Task AddItemAsync_MovieEpisodeAndSpecial_AreReturnedInOrder_WithCorrectKind()
    {
        Special special = SeedSpecial();

        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        PlaylistItem? movieItem = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(129)
        );
        PlaylistItem? episodeItem = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForEpisode(62085)
        );
        PlaylistItem? specialItem = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForSpecial(special.Id)
        );

        Assert.NotNull(movieItem);
        Assert.NotNull(episodeItem);
        Assert.NotNull(specialItem);
        Assert.Equal(0, movieItem!.Order);
        Assert.Equal(1, episodeItem!.Order);
        Assert.Equal(2, specialItem!.Order);

        List<PlaylistItem>? items = await _repository.GetPlaylistItemsAsync(
            playlistId,
            SeedConstants.UserId,
            "en",
            "US"
        );

        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);

        Assert.Equal(PlaylistItemKind.Movie, items[0].Kind);
        Assert.Equal(129, items[0].MovieId);
        Assert.Equal("Spirited Away", items[0].Movie?.Title);

        Assert.Equal(PlaylistItemKind.Episode, items[1].Kind);
        Assert.Equal(62085, items[1].EpisodeId);
        Assert.Equal("Pilot", items[1].Episode?.Title);
        Assert.Equal(1399, items[1].Episode?.Tv.Id);

        Assert.Equal(PlaylistItemKind.Special, items[2].Kind);
        Assert.Equal(special.Id, items[2].SpecialId);
        Assert.Equal("Test Special", items[2].Special?.Title);
    }

    [Fact]
    public async Task AddItemAsync_WithExplicitOrder_InsertsAndShiftsExistingItems()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        PlaylistItem? first = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(129)
        );
        PlaylistItem? second = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(680)
        );

        // Insert a third movie at the front — 129 and 680 should both shift down.
        Special special = SeedSpecial();
        PlaylistItem? inserted = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForSpecial(special.Id),
            order: 0
        );

        Assert.NotNull(inserted);
        Assert.Equal(0, inserted!.Order);

        List<PlaylistItem>? items = await _repository.GetPlaylistItemsAsync(
            playlistId,
            SeedConstants.UserId,
            "en",
            "US"
        );

        Assert.NotNull(items);
        Assert.Equal(3, items!.Count);
        Assert.Equal(PlaylistItemKind.Special, items[0].Kind);
        Assert.Equal(special.Id, items[0].SpecialId);
        Assert.Equal(129, items[1].MovieId);
        Assert.Equal(1, items[1].Order);
        Assert.Equal(680, items[2].MovieId);
        Assert.Equal(2, items[2].Order);
    }

    [Fact]
    public async Task AddItemAsync_RejectsNonexistentMedia()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        PlaylistItem? result = await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(999_999)
        );

        Assert.Null(result);
        Assert.Empty(
            await _context.PlaylistItems.Where(pi => pi.UserPlaylistId == playlistId).ToListAsync()
        );
    }

    [Fact]
    public async Task RemoveItemAsync_RemovesOwnedItem()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem item = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;

        bool removed = await _repository.RemoveItemAsync(playlistId, SeedConstants.UserId, item.Id);

        Assert.True(removed);
        Assert.Null(await _context.PlaylistItems.FirstOrDefaultAsync(pi => pi.Id == item.Id));
    }

    [Fact]
    public async Task ReorderAsync_AppliesRequestedOrder()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem a = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;
        PlaylistItem b = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(680)
            )
        )!;

        bool reordered = await _repository.ReorderAsync(
            playlistId,
            SeedConstants.UserId,
            [b.Id, a.Id]
        );

        Assert.True(reordered);

        List<PlaylistItem>? items = await _repository.GetPlaylistItemsAsync(
            playlistId,
            SeedConstants.UserId,
            "en",
            "US"
        );
        Assert.NotNull(items);
        Assert.Equal(b.Id, items![0].Id);
        Assert.Equal(a.Id, items[1].Id);
    }

    [Fact]
    public async Task ReorderAsync_RejectsPartialItemList()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem a = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;
        await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(680)
        );

        // Omits the second item entirely — must be rejected, not silently applied.
        bool reordered = await _repository.ReorderAsync(playlistId, SeedConstants.UserId, [a.Id]);

        Assert.False(reordered);
    }

    [Fact]
    public async Task GetUserPlaylistsAsync_ReturnsOwnPlaylists_WithItemCount()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(
            SeedConstants.UserId,
            "Mix",
            cover: "/cover.jpg"
        );
        await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(129)
        );
        await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(680)
        );

        List<UserPlaylistSummary> playlists = await _repository.GetUserPlaylistsAsync(
            SeedConstants.UserId
        );

        UserPlaylistSummary summary = Assert.Single(playlists);
        Assert.Equal(playlistId, summary.Id);
        Assert.Equal("Mix", summary.Name);
        Assert.Equal("/cover.jpg", summary.Cover);
        Assert.Equal(2, summary.ItemCount);
    }

    #region Ownership isolation

    [Fact]
    public async Task GetPlaylistItemsAsync_ReturnsNull_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        await _repository.AddItemAsync(
            playlistId,
            SeedConstants.UserId,
            PlaylistItemRef.ForMovie(129)
        );

        List<PlaylistItem>? items = await _repository.GetPlaylistItemsAsync(
            playlistId,
            OtherUserId,
            "en",
            "US"
        );

        Assert.Null(items);
    }

    [Fact]
    public async Task AddItemAsync_ReturnsNull_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        PlaylistItem? result = await _repository.AddItemAsync(
            playlistId,
            OtherUserId,
            PlaylistItemRef.ForMovie(129)
        );

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveItemAsync_ReturnsFalse_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem item = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;

        bool removed = await _repository.RemoveItemAsync(playlistId, OtherUserId, item.Id);

        Assert.False(removed);
        Assert.NotNull(await _context.PlaylistItems.FirstOrDefaultAsync(pi => pi.Id == item.Id));
    }

    [Fact]
    public async Task ReorderAsync_ReturnsFalse_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem a = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;

        bool reordered = await _repository.ReorderAsync(playlistId, OtherUserId, [a.Id]);

        Assert.False(reordered);
    }

    [Fact]
    public async Task GetUserPlaylistsAsync_DoesNotReturnOtherUsersPlaylists()
    {
        await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mine");

        List<UserPlaylistSummary> otherUsersPlaylists = await _repository.GetUserPlaylistsAsync(
            OtherUserId
        );

        Assert.Empty(otherUsersPlaylists);
    }

    #endregion

    #region Playlist metadata — Owns/Get/Update/Delete

    [Fact]
    public async Task OwnsPlaylistAsync_ReturnsTrue_ForOwner_False_ForOtherUserOrUnknownId()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        Assert.True(await _repository.OwnsPlaylistAsync(playlistId, SeedConstants.UserId));
        Assert.False(await _repository.OwnsPlaylistAsync(playlistId, OtherUserId));
        Assert.False(await _repository.OwnsPlaylistAsync(Guid.NewGuid(), SeedConstants.UserId));
    }

    [Fact]
    public async Task GetPlaylistAsync_ReturnsMetadata_ForOwner_Null_ForOtherUser()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(
            SeedConstants.UserId,
            "Mix",
            "a description",
            "/cover.jpg"
        );

        UserPlaylistDetail? detail = await _repository.GetPlaylistAsync(
            playlistId,
            SeedConstants.UserId
        );

        Assert.NotNull(detail);
        Assert.Equal(playlistId, detail!.Id);
        Assert.Equal("Mix", detail.Name);
        Assert.Equal("a description", detail.Description);
        Assert.Equal("/cover.jpg", detail.Cover);

        Assert.Null(await _repository.GetPlaylistAsync(playlistId, OtherUserId));
    }

    [Fact]
    public async Task UpdatePlaylistAsync_AppliesOnlyProvidedFields()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(
            SeedConstants.UserId,
            "Original Name",
            "Original description",
            "/original.jpg"
        );

        // Only Name is provided — Description and Cover must be left untouched.
        bool updated = await _repository.UpdatePlaylistAsync(
            playlistId,
            SeedConstants.UserId,
            name: "Renamed",
            description: null,
            cover: null
        );

        Assert.True(updated);

        UserPlaylistDetail? detail = await _repository.GetPlaylistAsync(
            playlistId,
            SeedConstants.UserId
        );
        Assert.NotNull(detail);
        Assert.Equal("Renamed", detail!.Name);
        Assert.Equal("Original description", detail.Description);
        Assert.Equal("/original.jpg", detail.Cover);
    }

    [Fact]
    public async Task UpdatePlaylistAsync_ReturnsFalse_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        bool updated = await _repository.UpdatePlaylistAsync(
            playlistId,
            OtherUserId,
            name: "Hijacked",
            description: null,
            cover: null
        );

        Assert.False(updated);

        UserPlaylistDetail? detail = await _repository.GetPlaylistAsync(
            playlistId,
            SeedConstants.UserId
        );
        Assert.Equal("Mix", detail!.Name);
    }

    [Fact]
    public async Task DeletePlaylistAsync_RemovesPlaylistAndCascadesItems()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");
        PlaylistItem item = (
            await _repository.AddItemAsync(
                playlistId,
                SeedConstants.UserId,
                PlaylistItemRef.ForMovie(129)
            )
        )!;

        bool deleted = await _repository.DeletePlaylistAsync(playlistId, SeedConstants.UserId);

        Assert.True(deleted);
        Assert.Null(await _context.UserPlaylists.FirstOrDefaultAsync(p => p.Id == playlistId));
        Assert.Null(await _context.PlaylistItems.FirstOrDefaultAsync(pi => pi.Id == item.Id));
    }

    [Fact]
    public async Task DeletePlaylistAsync_ReturnsFalse_WhenCallerDoesNotOwnPlaylist()
    {
        Guid playlistId = await _repository.CreatePlaylistAsync(SeedConstants.UserId, "Mix");

        bool deleted = await _repository.DeletePlaylistAsync(playlistId, OtherUserId);

        Assert.False(deleted);
        Assert.NotNull(await _context.UserPlaylists.FirstOrDefaultAsync(p => p.Id == playlistId));
    }

    #endregion

    /// <summary>
    /// Proves the video-only UserPlaylist/PlaylistItem schema shares zero tables
    /// with the existing music-only Playlist/PlaylistTrack read path: a legacy
    /// music playlist created and read purely through PlaylistTrack still
    /// round-trips correctly via MusicRepository (direction one), and it never
    /// surfaces through UserPlaylistRepository — the video-only endpoint's own
    /// user (direction two, the leak the original implementation had).
    /// </summary>
    [Fact]
    public async Task ExistingMusicPlaylist_PlaylistTrackPath_StillWorks()
    {
        Track track = SeedTrack("Legacy Track");

        Playlist legacyPlaylist = new()
        {
            Id = Guid.NewGuid(),
            Name = "Legacy Music Playlist",
            UserId = SeedConstants.UserId,
        };
        _context.Playlists.Add(legacyPlaylist);
        _context.SaveChanges();

        _context.PlaylistTrack.Add(new(legacyPlaylist.Id, track.Id));
        _context.SaveChanges();

        MusicRepository musicRepository = new(_factory);
        List<PlaylistTrack> tracks = await musicRepository.GetPlaylistTracksAsync(
            SeedConstants.UserId,
            legacyPlaylist.Id
        );

        Assert.Single(tracks);
        Assert.Equal("Legacy Track", tracks[0].Track.Name);

        // Direction two: the same user's video-only playlist list is empty — the
        // music playlist never appears there, because the two features share no
        // table (UserPlaylists is a distinct table from Playlists, and there is
        // no UserPlaylist row with the music playlist's id).
        Assert.Empty(await _repository.GetUserPlaylistsAsync(SeedConstants.UserId));
        Assert.False(await _context.UserPlaylists.AnyAsync(p => p.Id == legacyPlaylist.Id));
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }
}
