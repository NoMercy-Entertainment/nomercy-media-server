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

using NoMercy.Api.DTOs.Common;
using NoMercy.NmSystem.Extensions;

namespace NoMercy.Api.DTOs.Media.Components;

/// <summary>
/// The version=lolomo answer for a movie, show or collection info page: one NMLolomo of credit
/// rows, in the order and with the captions of the native TV info screen (InfoComponentMapping.kt).
/// The page header keeps reading the unchanged info DTO.
/// </summary>
public static class InfoPage
{
    public static IEnumerable<ComponentEnvelope> Build(InfoResponseItemDto info) =>
        Build(
            info.MediaType,
            info.Id,
            [],
            info.Seasons,
            info.Writer,
            info.Director,
            info.Cast,
            info.Crew
        );

    public static IEnumerable<ComponentEnvelope> Build(CollectionResponseItemDto collection) =>
        Build(
            collection.MediaType,
            collection.Id,
            collection.Collection,
            [],
            null,
            null,
            collection.Cast,
            collection.Crew
        );

    private static IEnumerable<ComponentEnvelope> Build(
        string type,
        long id,
        IEnumerable<CollectionMovieDto> collection,
        IEnumerable<SeasonDto> seasons,
        PeopleDto? writer,
        PeopleDto? director,
        IEnumerable<PeopleDto> cast,
        IEnumerable<PeopleDto> crew
    )
    {
        List<ComponentEnvelope> rows = [];

        CardData[] collectionCards = Distinct(collection.Select(ToCard));
        if (collectionCards.Length > 0)
            rows.Add(Row("info-collection", "Collections".Localize(), collectionCards));
        else
            foreach (SeasonDto season in seasons)
                AddSeasonRow(rows, season);

        PeopleDto[] writerDirector = [.. new[] { writer, director }.OfType<PeopleDto>()];
        AddRow(
            rows,
            "info-director-writer",
            "Writer & Director".Localize(),
            writerDirector.Select(person => ToCard(person, person.Job ?? person.Character))
        );
        AddRow(
            rows,
            "info-cast",
            "Cast".Localize(),
            SortByProfileOrderName(cast).Select(person => ToCard(person, person.Character))
        );
        AddRow(
            rows,
            "info-crew",
            "Crew".Localize(),
            SortByProfileOrderName(crew).Select(person => ToCard(person, person.Job))
        );

        return
        [
            Component.Lolomo($"info-{type}-{id}").WithHero(LolomoProps.HeroNone).WithItems(rows),
        ];
    }

    private static void AddSeasonRow(List<ComponentEnvelope> rows, SeasonDto season)
    {
        CardData[] cards = Distinct(season.Episodes.Select(ToCard));
        if (cards.Length == 0)
            return;

        rows.Add(
            Component
                .Carousel()
                .WithId($"info-seasons-{season.SeasonNumber}")
                .WithTitle($"{season.SeasonNumber} - {season.Title}")
                .WithProperties(
                    new()
                    {
                        ["rowType"] = "seasons",
                        ["season_number"] = season.SeasonNumber.ToString(),
                    }
                )
                .WithItems(cards.Select(card => Component.Card().WithData(card)))
                .Build()
        );
    }

    private static void AddRow(
        List<ComponentEnvelope> rows,
        string id,
        string title,
        IEnumerable<CardData> cards
    )
    {
        CardData[] distinct = Distinct(cards);
        if (distinct.Length > 0)
            rows.Add(Row(id, title, distinct));
    }

    private static ComponentEnvelope Row(string id, string title, CardData[] cards) =>
        Component
            .Carousel()
            .WithId(id)
            .WithTitle(title)
            .WithItems(cards.Select(card => Component.Card().WithData(card)))
            .Build();

    private static CardData[] Distinct(IEnumerable<CardData> cards) =>
        [.. cards.DistinctBy(card => (string?)card.Id)];

    // Port of sortByFilteredAlphabetized: one entry per name, profiles first, then order, then name.
    private static IEnumerable<PeopleDto> SortByProfileOrderName(IEnumerable<PeopleDto> people)
    {
        PeopleDto[] unique = [.. people.DistinctBy(person => person.Name)];

        return unique
            .Where(person => person.ProfilePath is not null)
            .OrderBy(person => person.Order ?? int.MaxValue)
            .ThenBy(person => person.Name.ToLowerInvariant(), StringComparer.Ordinal)
            .Concat(
                unique
                    .Where(person => person.ProfilePath is null)
                    .OrderBy(person => person.Order ?? int.MaxValue)
                    .ThenBy(person => person.Name.ToLowerInvariant(), StringComparer.Ordinal)
            );
    }

    private static CardData ToCard(CollectionMovieDto movie) =>
        new()
        {
            Id = movie.Id.ToString(),
            Title = movie.Title.OrEmpty(),
            TitleSort = movie.TitleSort.OrEmpty(),
            Overview = movie.Overview,
            Link = movie.Link,
            Year = (int)movie.Year,
            Type = movie.MediaType,
            Backdrop = movie.Backdrop,
            Poster = movie.Poster,
            Logo = movie.Logo,
            ColorPalette = movie.ColorPalette,
            HaveItems = movie.HaveItems,
            NumberOfItems = movie.NumberOfItems,
        };

    private static CardData ToCard(PeopleDto person, string? subtitle) =>
        new()
        {
            Id = person.Id.ToString(),
            Title = person.Name,
            TitleSort = person.Name,
            Overview = person.Character ?? person.Job,
            Link = person.Link,
            Type = "person",
            Poster = person.ProfilePath,
            ColorPalette = person.ColorPalette,
            Subtitle = subtitle,
        };

    private static CardData ToCard(EpisodeDto episode) =>
        new()
        {
            Id = episode.Id.ToString(),
            Title = $"{episode.EpisodeNumber}. {episode.Title}",
            TitleSort = episode.Title.OrEmpty(),
            Overview = episode.Overview,
            Link = episode.Link,
            Type = "episode",
            Backdrop = episode.Still,
            ColorPalette = episode.ColorPalette,
        };
}
