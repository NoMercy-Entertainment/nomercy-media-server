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
using Moq;
using NoMercy.Data.Repositories;
using NoMercy.Data.Services.Music;
using Xunit;

namespace NoMercy.Tests.Repositories.Services.Music;

[Trait("Category", "Unit")]
public class MusicSearchTests
{
    private static readonly Guid UserId = Guid.NewGuid();

    private readonly Mock<IMusicRepository> _repository = new(MockBehavior.Loose);

    public MusicSearchTests()
    {
        _repository
            .Setup(r => r.SearchArtistIdsAsync(It.IsAny<string>(), default))
            .ReturnsAsync([]);
        _repository.Setup(r => r.SearchAlbumIdsAsync(It.IsAny<string>(), default)).ReturnsAsync([]);
        _repository
            .Setup(r => r.SearchPlaylistIdsAsync(It.IsAny<string>(), default))
            .ReturnsAsync([]);
        _repository.Setup(r => r.SearchTrackIdsAsync(It.IsAny<string>(), default)).ReturnsAsync([]);
    }

    [Fact]
    public async Task NoMatches_IsEmptyAndLoadsNoCards()
    {
        MusicSearchCards cards = await MusicSearch.FindCardsAsync(
            _repository.Object,
            "q",
            UserId,
            "NL"
        );

        cards.IsEmpty.Should().BeTrue();
        _repository.Verify(
            r => r.GetArtistCardsByIdsAsync(It.IsAny<List<Guid>>(), default),
            Times.Never
        );
    }

    [Fact]
    public async Task TrackMatch_BringsInItsArtistAndAlbum()
    {
        Guid trackId = Guid.NewGuid();
        Guid artistId = Guid.NewGuid();
        Guid albumId = Guid.NewGuid();
        _repository.Setup(r => r.SearchTrackIdsAsync("q", default)).ReturnsAsync([trackId]);
        _repository
            .Setup(r =>
                r.GetArtistIdsFromTracksAsync(
                    It.Is<List<Guid>>(ids => ids.Contains(trackId)),
                    default
                )
            )
            .ReturnsAsync([artistId]);
        _repository
            .Setup(r =>
                r.GetAlbumIdsFromTracksAsync(
                    It.Is<List<Guid>>(ids => ids.Contains(trackId)),
                    default
                )
            )
            .ReturnsAsync([albumId]);
        _repository
            .Setup(r => r.GetArtistCardsByIdsAsync(It.IsAny<List<Guid>>(), default))
            .ReturnsAsync([new ArtistCardDto()]);
        _repository
            .Setup(r => r.GetAlbumCardsByIdsAsync(It.IsAny<List<Guid>>(), default))
            .ReturnsAsync([new AlbumCardDto()]);
        _repository
            .Setup(r => r.SearchTrackCardsAsync(It.IsAny<List<Guid>>(), UserId, "NL", default))
            .ReturnsAsync([new SearchTrackCardDto()]);

        MusicSearchCards cards = await MusicSearch.FindCardsAsync(
            _repository.Object,
            "q",
            UserId,
            "NL"
        );

        cards.IsEmpty.Should().BeFalse();
        cards.Artists.Should().ContainSingle();
        cards.Albums.Should().ContainSingle();
        cards.Tracks.Should().ContainSingle();
        cards.Playlists.Should().BeEmpty();
        _repository.Verify(
            r =>
                r.GetArtistCardsByIdsAsync(
                    It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { artistId })),
                    default
                ),
            Times.Once
        );
        _repository.Verify(
            r =>
                r.GetAlbumCardsByIdsAsync(
                    It.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { albumId })),
                    default
                ),
            Times.Once
        );
    }
}
