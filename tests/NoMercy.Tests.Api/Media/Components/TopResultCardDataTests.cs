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

using NoMercy.Api.DTOs.Media.Components;
using NoMercy.Database.Models.Music;
using Xunit;

namespace NoMercy.Tests.Api.Media.Components;

[Trait("Category", "Unit")]
public class TopResultCardDataTests
{
    [Fact]
    public void ArtistCard_UsesArtistTypeAndLink()
    {
        Artist artist = new() { Id = Guid.NewGuid(), Name = "Test Artist" };

        TopResultCardData card = new(artist);

        card.Type.Should().Be("artist");
        card.Link.Should().Be($"/music/artists/{artist.Id}");
    }

    [Fact]
    public void FirstOf_WhenOnlyArtistMatches_UsesArtistTypeAndLink()
    {
        Artist artist = new() { Id = Guid.NewGuid(), Name = "Test Artist" };

        TopResultCardData? card = TopResultCardData.FirstOf(null, artist, null);

        card.Should().NotBeNull();
        card!.Type.Should().Be("artist");
        card.Link.Should().Be($"/music/artists/{artist.Id}");
    }
}
