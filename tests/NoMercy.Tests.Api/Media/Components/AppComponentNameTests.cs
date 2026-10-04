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
using NoMercy.Design;
using Xunit;

namespace NoMercy.Tests.Api.Media.Components;

/// <summary>
/// The app and a plugin call the media components by the same names.
///
/// <para>
/// The app uses <see cref="ComponentTypes"/>; a plugin cannot reference the web
/// project, so it uses <see cref="NmAppComponents"/>. Two lists of the same
/// fourteen strings drift, and a plugin then names a card no client draws — so
/// one now takes its values from the other, and this is where that is checked.
/// </para>
/// </summary>
[Trait("Category", "Unit")]
public class AppComponentNameTests
{
    [Fact]
    public void EveryNameThePluginContractPublishesIsOneTheAppDraws()
    {
        string[] appNames =
        [
            ComponentTypes.Grid,
            ComponentTypes.List,
            ComponentTypes.Carousel,
            ComponentTypes.Container,
            ComponentTypes.Card,
            ComponentTypes.HomeCard,
            ComponentTypes.GenreCard,
            ComponentTypes.MusicCard,
            ComponentTypes.MusicHomeCard,
            ComponentTypes.TrackRow,
            ComponentTypes.TopResultCard,
            ComponentTypes.GroupCard,
            ComponentTypes.SeasonCard,
            ComponentTypes.SeasonTitle,
            ComponentTypes.EmptyState,
        ];

        NmAppComponents.All.Should().BeEquivalentTo(appNames);
    }

    [Fact]
    public void ContainerAndLeafAgreeAcrossBothLists()
    {
        foreach (string component in NmAppComponents.All)
        {
            NmAppComponents
                .IsContainer(component)
                .Should()
                .Be(ComponentTypes.IsContainer(component), component);
        }
    }
}
