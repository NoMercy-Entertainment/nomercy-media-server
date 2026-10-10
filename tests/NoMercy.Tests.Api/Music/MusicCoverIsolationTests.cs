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

using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Moq;
using NoMercy.Api.Controllers.V1.Music;
using NoMercy.Data.Repositories;
using NoMercy.Database.Models.Music;
using NoMercy.Events;
using NoMercy.MediaProcessing.Images;
using NoMercy.MediaProcessing.Jobs;
using Xunit;

namespace NoMercy.Tests.Api.Music;

[Trait("Category", "Unit")]
public class MusicCoverIsolationTests
{
    [Fact]
    public async Task SameNamedArtistAndAlbum_KeepSeparateUploadedCovers()
    {
        Guid artistId = Guid.NewGuid();
        Guid albumId = Guid.NewGuid();
        Dictionary<string, byte[]> files = [];
        Mock<IMusicCoverStore> covers = CoverStore(files);
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r => r.GetArtistWithLibraryFolderAsync(artistId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Artist { Id = artistId, Name = "Same Name" });
        repository
            .Setup(r => r.GetAlbumWithLibraryFolderAsync(albumId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new Album { Id = albumId, Name = "Same Name" });
        Mock<IEventBus> events = new();
        Mock<IJobDispatcher> jobs = new();
        ArtistsController artists = new(
            Mock.Of<ILogger<ArtistsController>>(),
            repository.Object,
            events.Object,
            jobs.Object,
            covers.Object
        );
        AlbumsController albums = new(
            Mock.Of<ILogger<AlbumsController>>(),
            repository.Object,
            events.Object,
            jobs.Object,
            covers.Object
        );

        await artists.Cover(artistId, Image([1, 2, 3]));
        await albums.Cover(albumId, Image([4, 5, 6]));

        files.Should().HaveCount(2);
        files[$"artist-{artistId}.jpg"].Should().Equal(1, 2, 3);
        files[$"album-{albumId}.jpg"].Should().Equal(4, 5, 6);
    }

    [Fact]
    public async Task SameNamedPlaylistsFromDifferentUsers_KeepSeparateUploadedCovers()
    {
        Guid firstId = Guid.NewGuid();
        Guid secondId = Guid.NewGuid();
        Guid firstUser = Guid.NewGuid();
        Guid secondUser = Guid.NewGuid();
        Dictionary<string, byte[]> files = [];
        Mock<IMusicCoverStore> covers = CoverStore(files);
        Mock<IMusicRepository> repository = new();
        repository
            .Setup(r =>
                r.GetPlaylistForCoverAsync(firstId, firstUser, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new Playlist
                {
                    Id = firstId,
                    UserId = firstUser,
                    Name = "Same Name",
                }
            );
        repository
            .Setup(r =>
                r.GetPlaylistForCoverAsync(secondId, secondUser, It.IsAny<CancellationToken>())
            )
            .ReturnsAsync(
                new Playlist
                {
                    Id = secondId,
                    UserId = secondUser,
                    Name = "Same Name",
                }
            );
        Mock<IEventBus> events = new();
        Mock<IJobDispatcher> jobs = new();
        PlaylistsController first = PlaylistController(repository, events, jobs, covers, firstUser);
        PlaylistsController second = PlaylistController(
            repository,
            events,
            jobs,
            covers,
            secondUser
        );

        await first.Cover(firstId, Image([1, 2, 3]));
        await second.Cover(secondId, Image([4, 5, 6]));

        files.Should().HaveCount(2);
        files[$"playlist-{firstId}.jpg"].Should().Equal(1, 2, 3);
        files[$"playlist-{secondId}.jpg"].Should().Equal(4, 5, 6);
    }

    private static Mock<IMusicCoverStore> CoverStore(Dictionary<string, byte[]> files)
    {
        Mock<IMusicCoverStore> covers = new();
        covers
            .Setup(c =>
                c.SaveToLibraryAsync(
                    It.IsAny<NoMercy.Database.Models.Libraries.Folder>(),
                    It.IsAny<string>(),
                    It.IsAny<string>(),
                    It.IsAny<Stream>(),
                    It.IsAny<CancellationToken>()
                )
            )
            .ReturnsAsync(true);
        covers
            .Setup(c =>
                c.SaveAsync(It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<CancellationToken>())
            )
            .Returns(
                async (string key, Stream image, CancellationToken _) =>
                {
                    using MemoryStream copy = new();
                    await image.CopyToAsync(copy);
                    files[$"{key}.jpg"] = copy.ToArray();
                    return new SavedMusicCover($"/{key}.jpg", "{}");
                }
            );
        return covers;
    }

    private static IFormFile Image(byte[] content) =>
        new FormFile(new MemoryStream(content), 0, content.Length, "image", "cover.jpg");

    private static PlaylistsController PlaylistController(
        Mock<IMusicRepository> repository,
        Mock<IEventBus> events,
        Mock<IJobDispatcher> jobs,
        Mock<IMusicCoverStore> covers,
        Guid userId
    )
    {
        PlaylistsController controller = new(
            Mock.Of<ILogger<PlaylistsController>>(),
            repository.Object,
            events.Object,
            jobs.Object,
            covers.Object
        );
        controller.ControllerContext = new()
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(
                    new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())])
                ),
            },
        };
        return controller;
    }
}
