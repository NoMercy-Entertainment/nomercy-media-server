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

namespace NoMercy.Design;

/// <summary>
/// The components the app draws its own screens with.
///
/// <para>
/// <see cref="NmComponents"/> is the design system — the fifty-six components
/// generated from the manifest. These are the other set: the media components
/// the server's own home, library and music screens are built from, and every
/// client already renders them. A plugin gets the same set, because a plugin
/// showing a shelf of films has no reason to rebuild the shelf the app ships.
/// </para>
/// <para>
/// Names only. The props for these carry database-shaped data — a card is built
/// from a movie row — and those records stay in the web project on purpose: a
/// plugin must not be handed the data layer to draw a card. A plugin fills these
/// through <c>PluginComponent.Props</c>, the same way it fills anything the
/// design system does not type for it.
/// </para>
/// </summary>
public static class NmAppComponents
{
    public const string Grid = "NMGrid";

    public const string List = "NMList";

    public const string Carousel = "NMCarousel";

    public const string Container = "NMContainer";

    public const string Card = "NMCard";

    public const string HomeCard = "NMHomeCard";

    public const string GenreCard = "NMGenreCard";

    /// <summary>
    /// A group of titles with no image of its own, drawn from their posters. Sent
    /// only to an app that lists it; every other app gets <see cref="GenreCard"/>.
    /// </summary>
    public const string GroupCard = "NMGroupCard";

    public const string MusicCard = "NMMusicCard";

    public const string MusicHomeCard = "NMMusicHomeCard";

    public const string TrackRow = "NMTrackRow";

    public const string TopResultCard = "NMTopResultCard";

    public const string SeasonCard = "NMSeasonCard";

    public const string SeasonTitle = "NMSeasonTitle";

    public const string EmptyState = "NMEmptyState";

    /// <summary>Everything above, for a test that has to walk the whole set.</summary>
    public static IReadOnlyList<string> All { get; } =
    [
        Grid,
        List,
        Carousel,
        Container,
        Card,
        HomeCard,
        GenreCard,
        GroupCard,
        MusicCard,
        MusicHomeCard,
        TrackRow,
        TopResultCard,
        SeasonCard,
        SeasonTitle,
        EmptyState,
    ];

    /// <summary>
    /// Whether the component holds children. A leaf given children draws them
    /// after its own content rather than dropping them, but a payload that meant
    /// to nest is usually a payload that picked the wrong component.
    /// </summary>
    public static bool IsContainer(string component) =>
        component is Grid or List or Carousel or Container;
}
