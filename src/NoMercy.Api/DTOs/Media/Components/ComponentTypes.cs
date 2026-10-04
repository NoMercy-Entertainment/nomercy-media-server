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

using NoMercy.Design;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// Defines all available component types in the system.
/// Container types can hold child components, leaf types cannot.
/// </summary>
public static class ComponentTypes
{
    // Container components - can hold child components
    public const string Grid = NmAppComponents.Grid;
    public const string List = NmAppComponents.List;
    public const string Carousel = NmAppComponents.Carousel;
    public const string Container = NmAppComponents.Container;

    // Leaf components - cannot hold children
    public const string Card = NmAppComponents.Card;
    public const string HomeCard = NmAppComponents.HomeCard;
    public const string GenreCard = NmAppComponents.GenreCard;
    public const string GroupCard = NmAppComponents.GroupCard;
    public const string MusicCard = NmAppComponents.MusicCard;
    public const string MusicHomeCard = NmAppComponents.MusicHomeCard;
    public const string TrackRow = NmAppComponents.TrackRow;
    public const string TopResultCard = NmAppComponents.TopResultCard;
    public const string SeasonCard = NmAppComponents.SeasonCard;
    public const string SeasonTitle = NmAppComponents.SeasonTitle;
    public const string EmptyState = NmAppComponents.EmptyState;

    public static bool IsContainer(string componentType) =>
        componentType is Grid or List or Carousel or Container;

    public static bool IsLeaf(string componentType) => !IsContainer(componentType);
}
