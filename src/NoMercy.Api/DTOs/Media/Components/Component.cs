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

using NoMercy.Api.DTOs.Music;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Factory for creating component envelopes with proper typing and validation.
/// Supports fluent API for building both container and leaf components.
/// </summary>
public static class Component
{
    #region Container Components

    /// <summary>
    /// Creates an NMGrid component - displays items in a grid layout.
    /// </summary>
    public static ContainerComponentBuilder Grid() => new(ComponentTypes.Grid, new GridProps());

    /// <summary>
    /// Creates an NMList component - displays items in a vertical list.
    /// </summary>
    public static ContainerComponentBuilder List() => new(ComponentTypes.List, new ListProps());

    /// <summary>
    /// Creates an NMCarousel component - displays items in a horizontal scrollable carousel.
    /// </summary>
    public static ContainerComponentBuilder Carousel() =>
        new(ComponentTypes.Carousel, new CarouselProps());

    /// <summary>
    /// Creates an NMContainer component - generic container for grouping components.
    /// </summary>
    public static ContainerComponentBuilder Container() =>
        new(ComponentTypes.Container, new NmContainerProps());

    #endregion

    #region Leaf Components

    /// <summary>
    /// Creates an NMCard component - standard media card.
    /// </summary>
    public static LeafComponentBuilder<CardData> Card() => new(ComponentTypes.Card);

    /// <summary>
    /// Creates an NMCard component with data.
    /// </summary>
    public static LeafComponentBuilder<CardData> Card(CardData data) =>
        new LeafComponentBuilder<CardData>(ComponentTypes.Card).WithData(data);

    /// <summary>
    /// Creates an NMHomeCard component - featured home page card.
    /// </summary>
    public static LeafComponentBuilder<HomeCardData> HomeCard() => new(ComponentTypes.HomeCard);

    /// <summary>
    /// Creates an NMHomeCard component with data.
    /// </summary>
    public static LeafComponentBuilder<HomeCardData> HomeCard(HomeCardData data) =>
        new LeafComponentBuilder<HomeCardData>(ComponentTypes.HomeCard).WithData(data);

    /// <summary>
    /// Creates an NMGenreCard component - genre category card.
    /// </summary>
    public static LeafComponentBuilder<GenreCardData> GenreCard() => new(ComponentTypes.GenreCard);

    /// <summary>
    /// Creates an NMGenreCard component with data.
    /// </summary>
    public static LeafComponentBuilder<NmGenreCardDto> GenreCard(NmGenreCardDto data) =>
        new LeafComponentBuilder<NmGenreCardDto>(ComponentTypes.GenreCard).WithData(data);

    /// <summary>
    /// Creates an NMMusicCard component - music album/artist card.
    /// </summary>
    public static LeafComponentBuilder<MusicCardData> MusicCard() => new(ComponentTypes.MusicCard);

    /// <summary>
    /// Creates an NMMusicHomeCard component - music home featured card.
    /// </summary>
    public static LeafComponentBuilder<MusicHomeCardData> MusicHomeCard() =>
        new(ComponentTypes.MusicHomeCard);

    /// <summary>
    /// Creates an NMMusicHomeCard component with data.
    /// </summary>
    public static LeafComponentBuilder<MusicHomeCardData> MusicHomeCard(MusicHomeCardData data) =>
        new LeafComponentBuilder<MusicHomeCardData>(ComponentTypes.MusicHomeCard).WithData(data);

    /// <summary>
    /// Creates an NMTrackRow component - single track in a list.
    /// </summary>
    public static TrackRowComponentBuilder TrackRow() => new();

    /// <summary>
    /// Creates an NMTrackRow component with data.
    /// </summary>
    public static TrackRowComponentBuilder TrackRow(TrackRowData data) =>
        new TrackRowComponentBuilder().WithData(data);

    /// <summary>
    /// Creates an NMTopResultCard component - search top result.
    /// </summary>
    public static LeafComponentBuilder<TopResultCardData> TopResultCard() =>
        new(ComponentTypes.TopResultCard);

    /// <summary>
    /// Creates an NMTopResultCard component with data.
    /// </summary>
    public static LeafComponentBuilder<TopResultCardData> TopResultCard(TopResultCardData data) =>
        new LeafComponentBuilder<TopResultCardData>(ComponentTypes.TopResultCard).WithData(data);

    /// <summary>
    /// Creates an NMSeasonCard component - episode in a season.
    /// </summary>
    public static LeafComponentBuilder<SeasonCardData> SeasonCard() =>
        new(ComponentTypes.SeasonCard);

    /// <summary>
    /// Creates an NMSeasonCard component with data.
    /// </summary>
    public static LeafComponentBuilder<SeasonCardData> SeasonCard(SeasonCardData data) =>
        new LeafComponentBuilder<SeasonCardData>(ComponentTypes.SeasonCard).WithData(data);

    /// <summary>
    /// Creates an NMSeasonTitle component - season header.
    /// </summary>
    public static LeafComponentBuilder<SeasonTitleData> SeasonTitle() =>
        new(ComponentTypes.SeasonTitle);

    /// <summary>
    /// Creates an NMSeasonTitle component with data.
    /// </summary>
    public static LeafComponentBuilder<SeasonTitleData> SeasonTitle(SeasonTitleData data) =>
        new LeafComponentBuilder<SeasonTitleData>(ComponentTypes.SeasonTitle).WithData(data);

    /// <summary>
    /// Creates an NMEmptyState component - shown when there is no content to display.
    /// </summary>
    public static LeafComponentBuilder<EmptyStateData> EmptyState() =>
        new(ComponentTypes.EmptyState);

    /// <summary>
    /// Creates an NMEmptyState component with data.
    /// </summary>
    public static LeafComponentBuilder<EmptyStateData> EmptyState(EmptyStateData data) =>
        new LeafComponentBuilder<EmptyStateData>(ComponentTypes.EmptyState).WithData(data);

    #endregion

    public static ComponentEnvelope MusicCard(ArtistsResponseItemDto data) =>
        new LeafComponentBuilder<ArtistsResponseItemDto>(ComponentTypes.MusicCard).WithData(data);

    public static ComponentEnvelope MusicCard(AlbumsResponseItemDto data) =>
        new LeafComponentBuilder<AlbumsResponseItemDto>(ComponentTypes.MusicCard).WithData(data);

    public static ComponentEnvelope MusicCard(PlaylistResponseItemDto data) =>
        new LeafComponentBuilder<PlaylistResponseItemDto>(ComponentTypes.MusicCard).WithData(data);

    /// <summary>
    /// Creates an NMMusicCard component with data.
    /// </summary>
    public static ComponentEnvelope MusicCard(MusicCardData data) =>
        new LeafComponentBuilder<MusicCardData>(ComponentTypes.MusicCard).WithData(data);
}
