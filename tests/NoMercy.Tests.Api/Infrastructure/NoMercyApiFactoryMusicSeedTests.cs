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

using Microsoft.EntityFrameworkCore;
using NoMercy.Database;
using Xunit;

namespace NoMercy.Tests.Api.Infrastructure;

public class NoMercyApiFactoryMusicSeedTests : IClassFixture<NoMercyApiFactory>
{
    public NoMercyApiFactoryMusicSeedTests(NoMercyApiFactory factory) => _ = factory;

    [Fact]
    public async Task SeededAlbum_BelongsToLibraryGrantedToDefaultUser()
    {
        await using MediaContext context = new();

        int accessibleAlbumCount = await context
            .Albums.AsNoTracking()
            .Where(album => album.Id == NoMercyApiFactory.AlbumId1)
            .CountAsync(album =>
                album.Library.LibraryUsers.Any(libraryUser =>
                    libraryUser.UserId == TestAuthHandler.DefaultUserId
                )
            );
        Assert.Equal(1, accessibleAlbumCount);

        Ulid storedLibraryId = await context
            .Albums.AsNoTracking()
            .Where(album => album.Id == NoMercyApiFactory.AlbumId1)
            .Select(album => album.LibraryId)
            .SingleAsync();
        Assert.Equal(NoMercyApiFactory.MusicLibraryId, storedLibraryId);
    }
}
